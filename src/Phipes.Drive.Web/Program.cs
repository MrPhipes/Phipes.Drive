using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Phipes.Drive.Core.Photos;
using Phipes.Drive.Core.Storage;
using Phipes.Drive.Web.Components;
using Phipes.Drive.Web.Data;
using Phipes.Drive.Web.Photos;

var builder = WebApplication.CreateBuilder(args);

// Chunked uploads from mobile clients: allow bodies up to 256 MB per request.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256L * 1024 * 1024);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=phipes-drive.db"));

// Cookie auth for the browser + bearer tokens (with refresh) for phones and API clients,
// selected automatically per request by the Identity composite scheme.
builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<AppUser>(o =>
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<AppDbContext>();

// Challenge with the cookie scheme so browsers get redirected to /login;
// the event below keeps /api responses as plain 401 for token clients.
builder.Services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
    o.DefaultChallengeScheme = IdentityConstants.ApplicationScheme);
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/login";
    o.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else
            ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
});

var storageOptions = builder.Configuration
    .GetSection(StorageOptions.SectionName)
    .Get<StorageOptions>() ?? new StorageOptions();
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton<IFileStorage, FileSystemStorage>();

// ---- Photo pipeline ----
// Thumbnails live outside the storage root so they never appear in the user's file browser.
var photoOptions = builder.Configuration
    .GetSection(PhotoOptions.SectionName)
    .Get<PhotoOptions>() ?? new PhotoOptions();
var thumbsRoot = ThumbnailPaths.ResolveRoot(storageOptions.RootPath, photoOptions.ThumbsPath);

builder.Services.AddSingleton(photoOptions);
builder.Services.AddSingleton(new Ffmpeg(photoOptions));
builder.Services.AddSingleton<IPhotoMetadataReader>(sp =>
    new ExifPhotoMetadataReader(sp.GetRequiredService<Ffmpeg>()));
builder.Services.AddSingleton<IThumbnailGenerator>(sp =>
    new ThumbnailGenerator(photoOptions, sp.GetRequiredService<Ffmpeg>(), thumbsRoot));
builder.Services.AddSingleton<ThumbnailQueue>();
builder.Services.AddSingleton<IThumbnailQueue>(sp => sp.GetRequiredService<ThumbnailQueue>());
builder.Services.AddSingleton<PhotoScanTrigger>();
builder.Services.AddSingleton<PhotoQueryService>();
builder.Services.AddSingleton(sp => new ThumbnailService(
    sp.GetRequiredService<IFileStorage>(),
    sp.GetRequiredService<IThumbnailGenerator>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    thumbsRoot,
    sp.GetRequiredService<ILogger<ThumbnailService>>()));

builder.Services.AddScoped<IPhotoStore, EfPhotoStore>();
builder.Services.AddScoped(sp => new PhotoIndexer(
    sp.GetRequiredService<IFileStorage>(),
    sp.GetRequiredService<IPhotoStore>(),
    sp.GetRequiredService<IPhotoMetadataReader>(),
    sp.GetRequiredService<IThumbnailQueue>(),
    sp.GetRequiredService<ILogger<PhotoIndexer>>(),
    thumbsRoot));

builder.Services.AddHostedService<PhotoIndexBackgroundService>();
builder.Services.AddHostedService<ThumbnailBackgroundService>();

var app = builder.Build();

Directory.CreateDirectory(thumbsRoot);

// First-run setup: create the database and the admin user declared in configuration.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    // EnsureCreated is a no-op on databases that already exist, so tables added after the first
    // release need their own idempotent bootstrap.
    await PhotoSchema.EnsureAsync(db);

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    if (!userManager.Users.Any())
    {
        var email = app.Configuration["Admin:Email"];
        var password = app.Configuration["Admin:Password"];
        if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password))
        {
            var admin = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await userManager.CreateAsync(admin, password);
            if (!created.Succeeded)
                throw new InvalidOperationException(
                    "No se pudo crear el usuario admin: " +
                    string.Join("; ", created.Errors.Select(e => e.Description)));
        }
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Identity API (/api/identity/login, /refresh, /manage/info, ...) for token clients.
// Registration stays closed once the first user exists.
app.MapGroup("/api/identity")
    .MapIdentityApi<AppUser>()
    .AddEndpointFilter(async (ctx, next) =>
    {
        if (ctx.HttpContext.Request.Path.Value?.EndsWith("/register", StringComparison.OrdinalIgnoreCase) == true)
        {
            var users = ctx.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
            if (users.Users.Any())
                return Results.Forbid();
        }
        return await next(ctx);
    });

app.MapPost("/auth/logout", async (SignInManager<AppUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.LocalRedirect("/login");
});

// File API for mobile/script clients (bearer or cookie). The storage layer validates paths.
app.MapGet("/api/files/stat", async (string path, IFileStorage storage, CancellationToken ct) =>
{
    try
    {
        var item = await storage.StatAsync(path, ct);
        return item is null
            ? Results.Ok(new { exists = false })
            : Results.Ok(new { exists = true, item.IsFolder, item.SizeBytes, item.ModifiedUtc });
    }
    catch (StoragePathException) { return Results.BadRequest(); }
}).RequireAuthorization();

app.MapPost("/api/files", async (string path, bool? overwrite, bool? append, HttpRequest request, IFileStorage storage, CancellationToken ct) =>
{
    try
    {
        if (append is true)
        {
            await storage.AppendAsync(path, request.Body, ct);
            var appended = await storage.StatAsync(path, ct);
            return Results.Ok(new { path, appended!.SizeBytes });
        }
        if (overwrite is not true && await storage.StatAsync(path, ct) is not null)
            return Results.Conflict(new { error = "El archivo ya existe.", path });
        await storage.SaveAsync(path, request.Body, ct);
        var saved = await storage.StatAsync(path, ct);
        return Results.Created($"/api/download?path={Uri.EscapeDataString(path)}", new { path, saved!.SizeBytes });
    }
    catch (StoragePathException) { return Results.BadRequest(); }
    catch (StorageQuotaExceededException) { return Results.StatusCode(StatusCodes.Status507InsufficientStorage); }
}).RequireAuthorization().DisableAntiforgery();

// Streamed download endpoint; the storage layer validates the path.
NoSniff(app.MapGet("/api/download", async (string path, IFileStorage storage, CancellationToken ct) =>
{
    try
    {
        var stream = await storage.OpenReadAsync(path, ct);
        var fileName = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
        if (!new FileExtensionContentTypeProvider().TryGetContentType(fileName, out var contentType))
            contentType = "application/octet-stream";
        return Results.Stream(stream, contentType, fileName, enableRangeProcessing: true);
    }
    catch (Exception e) when (e is FileNotFoundException or StoragePathException)
    {
        return Results.NotFound();
    }
})).RequireAuthorization();

// ---- Photo gallery API ----
var photos = app.MapGroup("/api/photos").RequireAuthorization();

// Timeline, newest capture first. "since" filters on IndexedUtc so a client can pull only what
// the server has learned since its last sync, including old photos imported late.
photos.MapGet("", async (
    DateTimeOffset? since, int? skip, int? take, PhotoQueryService query, CancellationToken ct) =>
    Results.Ok(await query.ListAsync(since, skip ?? 0, take ?? 200, ct)));

NoSniff(photos.MapGet("/{id}/thumb", async (
    string id, int? size, PhotoQueryService query, ThumbnailService thumbnails,
    HttpContext http, CancellationToken ct) =>
{
    var requested = size ?? ThumbnailPaths.SmallSize;
    if (!ThumbnailPaths.IsSupportedSize(requested))
        return Results.BadRequest(new { error = "size must be 256 or 1024." });

    if (await query.GetAsync(id, ct) is not { } item) return Results.NotFound();

    var file = await thumbnails.EnsureAsync(item, requested, ct);
    if (file is null) return Results.NotFound();

    // The hash is the content, so a thumbnail can never change under the same URL.
    http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
    return Results.File(file, "image/jpeg",
        entityTag: new EntityTagHeaderValue($"\"{item.Hash}-{requested}\""));
}));

NoSniff(photos.MapGet("/{id}/original", async (
    string id, PhotoQueryService query, IFileStorage storage, HttpContext http, CancellationToken ct) =>
{
    if (await query.GetAsync(id, ct) is not { } item) return Results.NotFound();

    try
    {
        var stream = await storage.OpenReadAsync(item.Path, ct);
        if (!new FileExtensionContentTypeProvider().TryGetContentType(item.Name, out var contentType))
            contentType = "application/octet-stream";

        // Inline so the browser and the mobile viewer render it instead of downloading it.
        http.Response.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("inline") { FileNameStar = item.Name }.ToString();
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";

        return Results.Stream(stream, contentType,
            lastModified: item.ModifiedUtc,
            entityTag: new EntityTagHeaderValue($"\"{item.Hash}\""),
            enableRangeProcessing: true);
    }
    catch (Exception e) when (e is FileNotFoundException or StoragePathException)
    {
        return Results.NotFound();
    }
}));

photos.MapPost("/reindex", (PhotoScanTrigger trigger) =>
{
    trigger.RequestScan();
    return Results.Accepted();
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return;

// Blocks content-type sniffing on endpoints that stream user-supplied bytes.
static RouteHandlerBuilder NoSniff(RouteHandlerBuilder builder) =>
    builder.AddEndpointFilter(async (context, next) =>
    {
        context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return await next(context);
    });
