# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first so dependency layers are cached between builds
COPY global.json ./
COPY src/Phipes.Drive.Core/Phipes.Drive.Core.csproj src/Phipes.Drive.Core/
COPY src/Phipes.Drive.Web/Phipes.Drive.Web.csproj src/Phipes.Drive.Web/
RUN dotnet restore src/Phipes.Drive.Web/Phipes.Drive.Web.csproj

COPY src/ src/
RUN dotnet publish src/Phipes.Drive.Web/Phipes.Drive.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Default data locations; override via environment variables if needed.
# Mounted volumes must be writable by the non-root "app" user (UID 1654).
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__RootPath=/data/storage \
    ConnectionStrings__Default="Data Source=/data/db/phipes-drive.db"

RUN mkdir -p /data/storage /data/db && chown -R app:app /data
USER app

EXPOSE 8080
ENTRYPOINT ["dotnet", "Phipes.Drive.Web.dll"]
