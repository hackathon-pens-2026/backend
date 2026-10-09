using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SignIt.Infrastructure.Configuration;

namespace SignIt.Infrastructure.Persistence;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Load the local, gitignored .env when present; deployments set real variables instead.
        DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
        // Scaffolding and SQL generation do not connect to this placeholder database.
        // Applying migrations must supply the actual secret through the environment.
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5434;Database=hackathondb;Username=appuser";
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
    }
}
