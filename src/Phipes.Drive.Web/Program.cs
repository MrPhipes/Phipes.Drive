using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Phipes.Drive.Core.Storage;
using Phipes.Drive.Web.Components;
using Phipes.Drive.Web.Data;

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

var app = builder.Build();

// First-run setup: create the database and the admin user declared in configuration.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

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
app.MapGet("/api/download", async (string path, IFileStorage storage, CancellationToken ct) =>
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
}).RequireAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
