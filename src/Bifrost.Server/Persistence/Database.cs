using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Bifrost.Server.Security;

namespace Bifrost.Server.Persistence;

/// <summary>Opens Bifröst's SQLite database (a private file, or a throwaway in-memory one for tests).</summary>
public static class Database
{
    public static BifrostDbFactory OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            SecretFile.CreateNew(path, ""); // an empty file is a valid new SQLite database, created 0600
        }
        else if (!OperatingSystem.IsWindows())
        {
            var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (File.GetUnixFileMode(path) != ownerOnly) File.SetUnixFileMode(path, ownerOnly);
        }
        return new BifrostDbFactory($"Data Source={path};Pooling=False", keepAlive: null);
    }

    public static BifrostDbFactory OpenMemory()
    {
        var connectionString = $"Data Source=file:bifrost-{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open(); // the database lives as long as one connection stays open
        return new BifrostDbFactory(connectionString, keepAlive);
    }

    public static async Task MigrateAsync(IDbContextFactory<BifrostDb> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }
}

public sealed class BifrostDbFactory : IDbContextFactory<BifrostDb>, IDisposable
{
    private readonly DbContextOptions<BifrostDb> _options;
    private readonly SqliteConnection? _keepAlive;

    internal BifrostDbFactory(string connectionString, SqliteConnection? keepAlive)
    {
        _keepAlive = keepAlive;
        _options = new DbContextOptionsBuilder<BifrostDb>()
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
    }

    public BifrostDb CreateDbContext() => new(_options);

    public Task<BifrostDb> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public void Dispose() => _keepAlive?.Dispose();
}
