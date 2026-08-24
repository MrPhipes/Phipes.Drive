using Microsoft.EntityFrameworkCore;

namespace Phipes.Drive.Web.Data;

/// <summary>
/// The app provisions its database with <c>EnsureCreated</c>, which does nothing once the file
/// exists. These idempotent statements add the photo tables to databases created before the
/// gallery shipped; on a fresh database EnsureCreated has already made them and this is a no-op.
/// </summary>
public static class PhotoSchema
{
    private const string Sql = """
        -- Timestamps are UTC ticks (INTEGER) because SQLite cannot order DateTimeOffset text.
        CREATE TABLE IF NOT EXISTS "Photos" (
            "Hash" TEXT NOT NULL CONSTRAINT "PK_Photos" PRIMARY KEY,
            "Path" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "SizeBytes" INTEGER NOT NULL,
            "ModifiedUtc" INTEGER NOT NULL,
            "TakenUtc" INTEGER NOT NULL,
            "TakenFromExif" INTEGER NOT NULL,
            "Width" INTEGER NULL,
            "Height" INTEGER NULL,
            "Latitude" REAL NULL,
            "Longitude" REAL NULL,
            "IsVideo" INTEGER NOT NULL,
            "DurationMs" INTEGER NULL,
            "HasThumb" INTEGER NOT NULL,
            "ThumbFailed" INTEGER NOT NULL,
            "IndexedUtc" INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Photos_Path" ON "Photos" ("Path");
        CREATE INDEX IF NOT EXISTS "IX_Photos_TakenUtc" ON "Photos" ("TakenUtc");
        CREATE INDEX IF NOT EXISTS "IX_Photos_IndexedUtc" ON "Photos" ("IndexedUtc");
        CREATE TABLE IF NOT EXISTS "PhotoScanStates" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_PhotoScanStates" PRIMARY KEY,
            "LastScanUtc" INTEGER NULL,
            "LastScanDurationMs" INTEGER NOT NULL,
            "LastScanFileCount" INTEGER NOT NULL,
            "LastScanIndexedCount" INTEGER NOT NULL
        );
        """;

    public static Task EnsureAsync(AppDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(Sql, ct);
}
