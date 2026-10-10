using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mattiverse.Core.Data;

/// <summary>
/// Design-time factory used by the EF Core tools (<c>dotnet ef migrations …</c>).
/// It builds the context against a throwaway SQLite file and a system user accessor,
/// so migrations can be created without booting the full web host.
/// </summary>
public sealed class MattiverseDbContextFactory : IDesignTimeDbContextFactory<MattiverseDbContext>
{
    public MattiverseDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MattiverseDbContext>()
            .UseSqlite("Data Source=mattiverse-design.db")
            .Options;

        return new MattiverseDbContext(options, new SystemCurrentUserAccessor());
    }
}
