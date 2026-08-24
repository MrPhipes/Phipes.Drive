using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Phipes.Drive.Core.Photos;

namespace Phipes.Drive.Web.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<PhotoItem> Photos => Set<PhotoItem>();

    public DbSet<PhotoScanState> PhotoScanStates => Set<PhotoScanState>();

    // SQLite cannot sort or compare DateTimeOffset, so photo timestamps are stored as UTC ticks.
    private static readonly ValueConverter<DateTimeOffset, long> UtcTicks =
        new(value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableUtcTicks =
        new(value => value == null ? null : value.Value.UtcTicks,
            ticks => ticks == null ? null : new DateTimeOffset(ticks.Value, TimeSpan.Zero));

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PhotoItem>(entity =>
        {
            entity.ToTable("Photos");
            entity.HasKey(p => p.Hash);
            entity.Property(p => p.Hash).HasMaxLength(64);
            entity.Property(p => p.Path).IsRequired();
            entity.Property(p => p.Name).IsRequired();
            entity.Property(p => p.ModifiedUtc).HasConversion(UtcTicks);
            entity.Property(p => p.TakenUtc).HasConversion(UtcTicks);
            entity.Property(p => p.IndexedUtc).HasConversion(UtcTicks);
            // One row per distinct content, and one row per path.
            entity.HasIndex(p => p.Path).IsUnique();
            entity.HasIndex(p => p.TakenUtc);
            entity.HasIndex(p => p.IndexedUtc);
        });

        builder.Entity<PhotoScanState>(entity =>
        {
            entity.ToTable("PhotoScanStates");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.LastScanUtc).HasConversion(NullableUtcTicks);
        });
    }
}
