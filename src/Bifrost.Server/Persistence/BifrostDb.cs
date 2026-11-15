using Bifrost.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Server.Persistence;

public class BifrostDb(DbContextOptions<BifrostDb> options) : DbContext(options)
{
    public DbSet<PersonEntity> People => Set<PersonEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<AuditEntity> Audit => Set<AuditEntity>();

    /// <summary>SQLite keeps no time zone, so every time read back is marked UTC (all are written as UTC).</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<PersonEntity>(person =>
        {
            person.ToTable("people");
            person.HasKey(p => p.Id);
            person.Property(p => p.DisplayName).HasMaxLength(40).IsRequired();
            person.Property(p => p.NameKey).IsRequired();
            person.HasIndex(p => p.NameKey).IsUnique();
            person.Property(p => p.Role).HasConversion(role => StoredWords.Role(role), text => StoredWords.Role(text));
        });

        modelBuilder.Entity<DeviceEntity>(device =>
        {
            device.ToTable("devices");
            device.HasKey(d => d.Id);
            device.Property(d => d.Name).IsRequired();
            device.Property(d => d.TokenHash).IsRequired();
            device.HasIndex(d => d.TokenHash).IsUnique();
            device.Property(d => d.Kind).HasConversion(kind => StoredWords.Kind(kind), text => StoredWords.Kind(text));
            device.HasOne<PersonEntity>().WithMany().HasForeignKey(d => d.PersonId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEntity>(audit =>
        {
            audit.ToTable("audit");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Kind).IsRequired();
            audit.HasIndex(a => a.At);
        });
    }
}

internal sealed class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
    v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

/// <summary>The design's words for roles and device kinds, as stored (and as sent over the API).</summary>
public static class StoredWords
{
    public static string Role(PersonRole role) => role switch
    {
        PersonRole.Admin => "admin",
        PersonRole.Member => "member",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static PersonRole Role(string text) => text switch
    {
        "admin" => PersonRole.Admin,
        "member" => PersonRole.Member,
        _ => throw new FormatException($"Unknown role '{text}'."),
    };

    public static string Kind(DeviceKind kind) => kind switch
    {
        DeviceKind.App => "app",
        DeviceKind.BrowserFallback => "browser-fallback",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static DeviceKind Kind(string text) => text switch
    {
        "app" => DeviceKind.App,
        "browser-fallback" => DeviceKind.BrowserFallback,
        _ => throw new FormatException($"Unknown device kind '{text}'."),
    };
}
