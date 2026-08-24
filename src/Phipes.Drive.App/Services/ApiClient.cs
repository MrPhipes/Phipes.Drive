using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Phipes.Drive.App.Services;

public sealed record TokenResponse(string? TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

public sealed record StatResponse(bool Exists, bool? IsFolder, long? SizeBytes, DateTime? ModifiedUtc);

/// <summary>One media item indexed by the server. Id is the SHA-256 of the content.</summary>
public sealed record PhotoDto(
    string Id,
    string Name,
    string Path,
    DateTimeOffset TakenUtc,
    long SizeBytes,
    int? Width,
    int? Height,
    bool IsVideo,
    long? DurationMs,
    bool HasThumb);

/// <summary>One page of the remote photo index, newest first.</summary>
public sealed record PhotoPage(int Total, IReadOnlyList<PhotoDto> Items);

/// <summary>The stored session is no longer valid; the user must log in again.</summary>
public sealed class SessionExpiredException() : ApiException("La sesión expiró. Inicia sesión nuevamente.");

/// <summary>The remote file already exists (409 on create without overwrite).</summary>
public sealed class RemoteConflictException() : ApiException("El archivo ya existe en el servidor.");

/// <summary>The server storage quota is full (507).</summary>
public sealed class QuotaExceededException() : ApiException("La cuota de almacenamiento del servidor está llena.");

/// <summary>The server does not expose the photo index yet (404 on /api/photos).</summary>
public sealed class PhotoIndexUnavailableException()
    : ApiException("Este servidor todavía no publica el índice de fotos.");

/// <summary>
/// A failure worth retrying: a 5xx/408/429 answer, a dropped socket, or a timeout. On a
/// phone these are routine -- Android cuts the app network when the screen goes off.
/// </summary>
public sealed class TransientApiException(string message) : ApiException(message);

public class ApiException(string message) : Exception(message);

/// <summary>
/// HTTP client for the Phipes.Drive server: login/refresh token handling plus the file API
/// (stat, create, append). Tokens live in SecureStorage; the access token is renewed
/// automatically before it expires and once more on a 401 response.
/// </summary>
public sealed class ApiClient
{
    private const string AccessTokenKey = "phipes.access_token";
    private const string RefreshTokenKey = "phipes.refresh_token";
    private const string ExpiresPrefKey = "phipes.access_expires_unix";
    private const string ServerPrefKey = "phipes.server_url";
    private const string EmailPrefKey = "phipes.account_email";
    public const string DefaultServerUrl = "https://drive.phipes.cl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Shared connection pool. PooledConnectionLifetime matters a lot on a phone: when the
    /// screen goes off Android can quietly tear down the app sockets, and reusing one of those
    /// dead connections is what surfaces as "Software caused connection abort".
    /// </summary>
    private static readonly SocketsHttpHandler Handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        MaxConnectionsPerServer = 4,
    };

    // No global timeout: each call brings its own, so a 16 MB chunk and a stat request are
    // not judged by the same clock.
    private readonly HttpClient _http = new(Handler, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessExpiresUtc = DateTimeOffset.MinValue;

    public string ServerUrl
    {
        get => Preferences.Default.Get(ServerPrefKey, DefaultServerUrl);
        private set => Preferences.Default.Set(ServerPrefKey, value);
    }

    public string AccountEmail
    {
        get => Preferences.Default.Get(EmailPrefKey, string.Empty);
        private set => Preferences.Default.Set(EmailPrefKey, value);
    }

    public async Task<bool> HasSessionAsync() =>
        !string.IsNullOrEmpty(await SecureStorage.Default.GetAsync(RefreshTokenKey));

    public async Task LoginAsync(string serverUrl, string email, string password, CancellationToken ct = default)
    {
        ServerUrl = NormalizeServerUrl(serverUrl);
        using var timeout = LinkedTimeout(ct, ShortTimeout);
        using var response = await _http.PostAsJsonAsync(
            $"{ServerUrl}/api/identity/login", new { email, password }, Json, timeout.Token);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ApiException("Correo o contraseña incorrectos.");
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"El servidor respondió {(int)response.StatusCode}.");

        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct)
                     ?? throw new ApiException("Respuesta de login inválida.");
        await StoreTokensAsync(tokens);
        AccountEmail = email;
    }

    public void Logout()
    {
        SecureStorage.Default.Remove(AccessTokenKey);
        SecureStorage.Default.Remove(RefreshTokenKey);
        Preferences.Default.Remove(ExpiresPrefKey);
        _accessToken = null;
        _accessExpiresUtc = DateTimeOffset.MinValue;
    }

    public async Task<StatResponse> StatAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendWithAuthRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get,
                $"{ServerUrl}/api/files/stat?path={Uri.EscapeDataString(path)}"),
            ct);

        if (IsTransientStatus(response.StatusCode))
            throw new TransientApiException($"stat respondió {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"stat falló ({(int)response.StatusCode}) para {path}.");

        return await response.Content.ReadFromJsonAsync<StatResponse>(Json, ct)
               ?? throw new ApiException($"Respuesta stat inválida para {path}.");
    }

    /// <summary>Creates the remote file with the given content. Fails with 409 if it exists.</summary>
    public Task CreateFileAsync(string path, HttpContent content, CancellationToken ct = default) =>
        UploadAsync($"{ServerUrl}/api/files?path={Uri.EscapeDataString(path)}&overwrite=false", content, ct);

    /// <summary>Appends the content to an existing remote file (resumable chunk upload).</summary>
    public Task AppendFileAsync(string path, HttpContent content, CancellationToken ct = default) =>
        UploadAsync($"{ServerUrl}/api/files?path={Uri.EscapeDataString(path)}&append=true", content, ct);

    private async Task UploadAsync(string url, HttpContent content, CancellationToken ct)
    {
        // Refresh proactively so a chunk is not interrupted by an expiring token. Upload
        // bodies stream from the media store and cannot be replayed, so no 401 retry here.
        var token = await GetValidAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var timeout = LinkedTimeout(ct, UploadTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TransientApiException("La subida excedió el tiempo de espera.");
        }

        using (response)
        {
            switch ((int)response.StatusCode)
            {
                case 409: throw new RemoteConflictException();
                case 507: throw new QuotaExceededException();
                case 401: throw new SessionExpiredException();
            }
            if (IsTransientStatus(response.StatusCode))
                throw new TransientApiException($"El servidor respondió {(int)response.StatusCode}.");
            if (!response.IsSuccessStatusCode)
                throw new ApiException($"La subida falló ({(int)response.StatusCode}).");
        }
    }

    /// <summary>5xx, 408 and 429 deserve another attempt; every other status is final.</summary>
    public static bool IsTransientStatus(HttpStatusCode status) =>
        (int)status >= 500 ||
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static CancellationTokenSource LinkedTimeout(CancellationToken ct, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(timeout);
        return source;
    }

    private async Task<HttpResponseMessage> SendWithAuthRetryAsync(
        Func<HttpRequestMessage> makeRequest,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        TimeSpan? timeout = null)
    {
        var budget = timeout ?? ShortTimeout;

        async Task<HttpResponseMessage> SendAsync()
        {
            var request = makeRequest();
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await GetValidAccessTokenAsync(ct));
            using var linked = LinkedTimeout(ct, budget);
            try
            {
                return await _http.SendAsync(request, completion, linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TransientApiException("La petición excedió el tiempo de espera.");
            }
        }

        var response = await SendAsync();
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // Access token was rejected: refresh once and retry.
        response.Dispose();
        await RefreshAsync(ct);
        return await SendAsync();
    }

    private async Task<string> GetValidAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is null)
        {
            _accessToken = await SecureStorage.Default.GetAsync(AccessTokenKey);
            var unix = Preferences.Default.Get(ExpiresPrefKey, 0L);
            _accessExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        if (_accessToken is null || DateTimeOffset.UtcNow >= _accessExpiresUtc - TimeSpan.FromMinutes(2))
            await RefreshAsync(ct);

        return _accessToken ?? throw new SessionExpiredException();
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we waited.
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpiresUtc - TimeSpan.FromMinutes(2))
                return;

            var refreshToken = await SecureStorage.Default.GetAsync(RefreshTokenKey)
                               ?? throw new SessionExpiredException();

            using var timeout = LinkedTimeout(ct, ShortTimeout);
            using var response = await _http.PostAsJsonAsync(
                $"{ServerUrl}/api/identity/refresh", new { refreshToken }, Json, timeout.Token);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                Logout();
                throw new SessionExpiredException();
            }
            if (!response.IsSuccessStatusCode)
                throw new ApiException($"No se pudo renovar la sesión ({(int)response.StatusCode}).");

            var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct)
                         ?? throw new ApiException("Respuesta de refresh inválida.");
            await StoreTokensAsync(tokens);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task StoreTokensAsync(TokenResponse tokens)
    {
        _accessToken = tokens.AccessToken;
        _accessExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn);
        await SecureStorage.Default.SetAsync(AccessTokenKey, tokens.AccessToken);
        await SecureStorage.Default.SetAsync(RefreshTokenKey, tokens.RefreshToken);
        Preferences.Default.Set(ExpiresPrefKey, _accessExpiresUtc.ToUnixTimeSeconds());
    }

    // ---------------------------------------------------------------- photo index

    /// <summary>Reads a page of the server photo index (newest first).</summary>
    /// <param name="since">Only items captured at or after this instant, when given.</param>
    public async Task<PhotoPage> GetPhotosAsync(
        DateTimeOffset? since, int skip, int take, CancellationToken ct = default)
    {
        var url = $"{ServerUrl}/api/photos?skip={skip}&take={take}";
        if (since is not null)
            url += $"&since={Uri.EscapeDataString(since.Value.UtcDateTime.ToString("O"))}";

        using var response = await SendWithAuthRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url), ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new PhotoIndexUnavailableException();
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"El índice de fotos respondió {(int)response.StatusCode}.");

        return await response.Content.ReadFromJsonAsync<PhotoPage>(Json, ct)
               ?? throw new ApiException("Respuesta del índice de fotos inválida.");
    }

    /// <summary>Downloads a thumbnail (256 or 1024 px). Thumbnails are immutable per id.</summary>
    public async Task<byte[]> GetThumbnailAsync(string id, int size, CancellationToken ct = default)
    {
        using var response = await SendWithAuthRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/api/photos/{id}/thumb?size={size}"),
            ct);

        if (!response.IsSuccessStatusCode)
            throw new ApiException($"La miniatura respondió {(int)response.StatusCode}.");

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Downloads the original bytes to <paramref name="destinationPath"/>, resuming with a
    /// Range request when a partial download is already there. Progress is 0..1.
    /// </summary>
    public async Task DownloadOriginalAsync(
        string id,
        string destinationPath,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var partialPath = destinationPath + ".part";
        var alreadyHave = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (expectedSize > 0 && alreadyHave >= expectedSize)
            alreadyHave = 0; // Bogus leftover; start over.

        using var response = await SendWithAuthRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{ServerUrl}/api/photos/{id}/original");
                if (alreadyHave > 0)
                    request.Headers.Range = new RangeHeaderValue(alreadyHave, null);
                return request;
            },
            ct, HttpCompletionOption.ResponseHeadersRead, DownloadTimeout);

        if (!response.IsSuccessStatusCode)
            throw new ApiException($"La descarga respondió {(int)response.StatusCode}.");

        // The server may ignore the Range header and send the whole file.
        var append = response.StatusCode == HttpStatusCode.PartialContent && alreadyHave > 0;
        var baseOffset = append ? alreadyHave : 0;
        var total = (response.Content.Headers.ContentLength ?? 0) + baseOffset;
        if (total <= 0)
            total = expectedSize;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(
                         partialPath, append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            var written = baseOffset;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (total > 0)
                    progress?.Report(Math.Min(1d, written / (double)total));
            }
        }

        if (File.Exists(destinationPath))
            File.Delete(destinationPath);
        File.Move(partialPath, destinationPath);
        progress?.Report(1d);
    }

    public static string NormalizeServerUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.Length == 0)
            return DefaultServerUrl;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        return url;
    }
}
