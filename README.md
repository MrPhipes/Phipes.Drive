# Phipes.Drive

Self-hosted personal cloud storage built with .NET 10. Own your files and photos on your own hardware — a NAS, a home server, or any box that runs Docker.

> **Status:** early development. Not ready for production use.

## Why

Cloud storage subscriptions charge monthly for space you may already own. Phipes.Drive turns that spare disk into your personal drive: capacity is whatever you give it, and your data never leaves your hardware.

## Planned features

**Phase 1 — Files**
- Web file browser (Blazor)
- Chunked, resumable uploads
- Share links with expiration
- Per-user quotas
- Trash / restore

**Phase 2 — Photos**
- Timeline gallery with thumbnails
- EXIF metadata extraction
- Duplicate detection by content hash
- Google Takeout import

**Phase 3 — Mobile backup**
- WebDAV endpoint so existing mobile apps can auto-upload photos in the background

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone <repo-url>
cd Phipes.Drive
dotnet run --project src/Phipes.Drive.Web
```

Storage location and limits are configured in `appsettings.json` (defaults to a local `data/` folder for development):

```json
{
  "Storage": {
    "RootPath": "data",
    "MaxQuotaBytes": null
  }
}
```

In production, point `RootPath` at a mounted volume (e.g. `/data` in Docker).

## Project layout

```
src/
  Phipes.Drive.Core   # Domain: entities, storage abstractions
  Phipes.Drive.Web    # Blazor Server web app
tests/
  Phipes.Drive.Tests  # Unit tests
```

## License

[MIT](LICENSE)
