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
- Timeline gallery with thumbnails — done, see [Photos](#photos)
- EXIF metadata extraction — done
- Duplicate detection by content hash — done
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

## Photos

A background service walks the storage tree, indexes every image
(`jpg jpeg png heic webp gif`) and video (`mp4 mov 3gp mkv`) it finds, and
builds thumbnails for them. It runs a few seconds after startup and then every
15 minutes, without blocking application start.

For each file it stores the SHA-256 of the content, the capture date (EXIF
`DateTimeOriginal`, falling back to the file's modification time), pixel
dimensions, GPS coordinates when present, and video duration. **The hash is the
identity**: the same bytes under two different paths produce a single timeline
entry. Scans are incremental — a file whose path, size and modification time
already match the index is never read again.

Thumbnails are JPEG (quality 80) at 256 px and 1024 px on the longest side,
with EXIF orientation applied. They are written **outside** the storage root so
they never show up in the file browser:

```
<ThumbsPath>/<first 2 chars of hash>/<hash>_<size>.jpg
```

`ThumbsPath` defaults to a `thumbs/` folder next to `Storage:RootPath`, and the
Docker image sets it to `/data/thumbs`. Video poster frames (and HEIC decoding)
need `ffmpeg`, which is installed in the image; without it those items are
simply indexed with no thumbnail instead of failing.

```json
{
  "Photos": {
    "Enabled": true,
    "ThumbsPath": null,
    "ScanIntervalMinutes": 15,
    "FfmpegPath": "ffmpeg",
    "FfprobePath": "ffprobe",
    "VideoFrameSeconds": 1,
    "JpegQuality": 80
  }
}
```

### Gallery API

All endpoints require authentication (cookie or bearer token).

```bash
# Timeline, newest capture first.
# "since" is an ISO-8601 instant compared against the server's index time, so a
# client can pull only what is new to it, including old photos imported late.
curl -H "Authorization: Bearer <accessToken>" \
  "http://localhost:5000/api/photos?since=2026-08-01T00:00:00Z&skip=0&take=200"
```

```json
{
  "total": 1842,
  "items": [
    {
      "id": "2758653646130c330dfa6b8722f31fab93867dc243a33e466b3f60e6b45b32fc",
      "name": "IMG_20260824_140311.jpg",
      "path": "Camera/2026/08/IMG_20260824_140311.jpg",
      "takenUtc": "2026-08-24T17:03:11+00:00",
      "sizeBytes": 3145728,
      "width": 4032,
      "height": 3024,
      "isVideo": false,
      "durationMs": null,
      "hasThumb": true
    }
  ]
}
```

| Endpoint | Notes |
| --- | --- |
| `GET /api/photos?since=&skip=0&take=200` | Timeline page, ordered by `takenUtc` descending. `take` is capped at 500. |
| `GET /api/photos/{id}/thumb?size=256\|1024` | `image/jpeg` with an `ETag` and `Cache-Control: private, max-age=31536000, immutable` — the id is the content hash, so the bytes can never change. Missing thumbnails are generated on the spot; `404` when the file cannot be decoded. |
| `GET /api/photos/{id}/original` | The original bytes, with range requests and `Content-Disposition: inline`. |
| `POST /api/photos/reindex` | Triggers an immediate scan and returns `202`. |

`/api/download`, `/api/photos/*/thumb` and `/api/photos/*/original` all send
`X-Content-Type-Options: nosniff`.

### Web gallery

`/fotos` shows the same timeline in the browser: a grid of 256 px thumbnails
grouped by month, loaded in pages, with a viewer that shows the 1024 px render
(or plays the video) and links to the original.

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

### Release signing

Create your own keystore once and keep it outside the repository — losing it
means you can never ship an update to already-installed devices:

```bash
keytool -genkeypair -v -keystore phipes-drive-release.keystore \
  -alias phipes-drive -keyalg RSA -keysize 4096 -validity 10950
```

Pass it at publish time; no signing values are stored in the project:

```bash
dotnet publish src/Phipes.Drive.App/Phipes.Drive.App.csproj -f net10.0-android -c Release \
  -p:PhipesKeyStore=<path-to-keystore> -p:PhipesKeyStorePass=<password>
```

A release signature makes updates install over the existing app and gives the
build a stable identity, but sideloaded APKs still show Android's "unknown
sources" prompt — only distribution through an app store removes that.

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
