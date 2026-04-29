using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ILD.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        var dataPath = Environment.GetEnvironmentVariable("ILD_DATA_PATH") ?? "./data";
        var dbPath = Path.Combine(dataPath, "ild.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
        return new AppDbContext(optionsBuilder.Options);
    }
}
