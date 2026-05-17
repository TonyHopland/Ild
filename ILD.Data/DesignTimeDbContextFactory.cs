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
            ?? "Host=localhost;Port=5432;Database=IldCore;Username=ild_core;Password=ild_core_password";
        optionsBuilder.UseNpgsql(connectionString, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", "public"));
        return new AppDbContext(optionsBuilder.Options);
    }
}
