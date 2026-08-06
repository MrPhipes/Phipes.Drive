using Microsoft.AspNetCore.StaticFiles;
using Phipes.Drive.Core.Storage;
using Phipes.Drive.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var storageOptions = builder.Configuration
    .GetSection(StorageOptions.SectionName)
    .Get<StorageOptions>() ?? new StorageOptions();
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton<IFileStorage, FileSystemStorage>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

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
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
