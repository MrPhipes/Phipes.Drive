using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Phipes.Drive.App.Services;

public sealed record TokenResponse(string? TokenType, string AccessToken, long ExpiresIn, string RefreshToken);

public sealed record StatResponse(bool Exists, bool? IsFolder, long? SizeBytes, DateTime? ModifiedUtc);

/// <summary>The stored session is no longer valid; the user must log in again.</summary>
public sealed class SessionExpiredException() : ApiException("La sesión expiró. Inicia sesión nuevamente.");

/// <summary>The remote file already exists (409 on create without overwrite).</summary>
public sealed class RemoteConflictException() : ApiException("El archivo ya existe en el servidor.");

/// <summary>The server storage quota is full (507).</summary>
public sealed class QuotaExceededException() : ApiException("La cuota de almacenamiento del servidor está llena.");

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

    private readonly HttpClient _http = new()
    {
        // Large chunk uploads over slow uplinks can take a long time.
        Timeout = TimeSpan.FromMinutes(30),
    };

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
        using var response = await _http.PostAsJsonAsync(
            $"{ServerUrl}/api/identity/login", new { email, password }, Json, ct);

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

        using var response = await _http.SendAsync(request, ct);
        switch ((int)response.StatusCode)
        {
            case 409: throw new RemoteConflictException();
            case 507: throw new QuotaExceededException();
            case 401: throw new SessionExpiredException();
        }
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"La subida falló ({(int)response.StatusCode}).");
    }

    private async Task<HttpResponseMessage> SendWithAuthRetryAsync(
        Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        var request = makeRequest();
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", await GetValidAccessTokenAsync(ct));
        var response = await _http.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // Access token was rejected: refresh once and retry.
        response.Dispose();
        await RefreshAsync(ct);
        request = makeRequest();
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", await GetValidAccessTokenAsync(ct));
        return await _http.SendAsync(request, ct);
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

            using var response = await _http.PostAsJsonAsync(
                $"{ServerUrl}/api/identity/refresh", new { refreshToken }, Json, ct);

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
