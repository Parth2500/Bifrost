using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bifrost.Server.Persistence;

/// <summary>Used only by <c>dotnet ef migrations</c>.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<BifrostDb>
{
    public BifrostDb CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<BifrostDb>()
            .UseSqlite("Data Source=design-time.db")
            .UseSnakeCaseNamingConvention()
            .Options);
}
