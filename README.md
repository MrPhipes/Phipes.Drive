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
dotnet user-secrets set "Admin:Email" "you@example.com" --project src/Phipes.Drive.Web
dotnet user-secrets set "Admin:Password" "YourStrongPassword1" --project src/Phipes.Drive.Web
dotnet run --project src/Phipes.Drive.Web
```

On first run the app creates a SQLite database and the admin user from the
`Admin:Email` / `Admin:Password` configuration (user secrets, environment
variables `Admin__Email` / `Admin__Password`, or appsettings). Registration is
closed once a user exists.

## Deploy with Docker

Every push to `main` publishes an image to GitHub Container Registry:
`ghcr.io/mrphipes/phipes-drive` (tags: `latest` and the short commit SHA).

Use the example [`docker-compose.yml`](docker-compose.yml) in this repo. Create
a `.env` file next to it with the admin credentials for the first run:

```bash
ADMIN_EMAIL=you@example.com
ADMIN_PASSWORD=YourStrongPassword1
```

Then start the app:

```bash
docker compose up -d
```

The app listens on port 8080. User files go to the `./storage` volume and the
SQLite database to `./db`. The container runs as a non-root user (UID 1654),
so make sure both folders are writable by it:

```bash
chown -R 1654:1654 ./storage ./db
```

## Authentication

Browsers use cookie sign-in via the `/login` page. API clients (the future
mobile backup app, scripts, integrations) use bearer tokens:

```bash
# Obtain tokens
curl -X POST http://localhost:5000/api/identity/login \
  -H "Content-Type: application/json" \
  -d '{"email":"you@example.com","password":"YourStrongPassword1"}'
# → { "accessToken": "...", "expiresIn": 3600, "refreshToken": "..." }

# Call the API
curl -H "Authorization: Bearer <accessToken>" \
  "http://localhost:5000/api/download?path=docs/report.pdf"

# Renew without re-sending credentials
curl -X POST http://localhost:5000/api/identity/refresh \
  -H "Content-Type: application/json" \
  -d '{"refreshToken":"<refreshToken>"}'
```

Storage location and limits are configured in `appsettings.json` (defaults to a local `storage/` folder for development):

```json
{
  "Storage": {
    "RootPath": "storage",
    "QuotaBytes": null
  }
}
```

In production, point `RootPath` at a mounted volume (e.g. `/data` in Docker).

## Mobile app

`src/Phipes.Drive.App` is a .NET MAUI app (Android-first) that backs up the
phone's photos and videos to the server: it enumerates the selected MediaStore
folders (DCIM/Camera by default), uploads each file to
`Camera/<yyyy>/<MM>/<name>` in resumable 64 MB chunks via the file API, skips
files already uploaded, and resumes partial uploads. It includes a Wi-Fi-only
toggle and an hourly WorkManager background backup.

The app is **intentionally not part of `Phipes.Drive.slnx`**: the solution is
built by CI and server machines that do not have the MAUI workloads installed.
Build the app directly against its project file instead:

```bash
# Requirements: .NET 10 SDK + Android/MAUI workloads (dotnet workload install maui-android)
dotnet build src/Phipes.Drive.App/Phipes.Drive.App.csproj -f net10.0-android

# Sideloadable APK (debug-signed)
dotnet publish src/Phipes.Drive.App/Phipes.Drive.App.csproj -f net10.0-android -c Release
```

The `net10.0-ios` target is declared for future work but only activates on a
macOS host with the `maui-ios` workload; the iOS media library backend is not
implemented yet.

## Project layout

```
src/
  Phipes.Drive.Core   # Domain: entities, storage abstractions
  Phipes.Drive.Web    # Blazor Server web app
  Phipes.Drive.App    # .NET MAUI mobile backup app (not in the solution; see above)
tests/
  Phipes.Drive.Tests  # Unit tests
```

## License

[MIT](LICENSE)
