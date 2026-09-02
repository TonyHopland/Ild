using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ILD.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        var connectionString = Environment.GetEnvironmentVariable("ILD_DB_CONNECTION_STRING")
            // No password: `migrations add` never connects, and anyone running
            // `database update` supplies the per-deployment one through the env var.
            ?? "Host=localhost;Port=5432;Database=IldCore;Username=ild_core";
        optionsBuilder.UseNpgsql(connectionString, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", "public"));
        return new AppDbContext(optionsBuilder.Options);
    }
}
