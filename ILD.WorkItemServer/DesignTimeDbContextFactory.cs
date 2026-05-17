using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ILD.WorkItemServer;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WorkItemServerDbContext>
{
    public WorkItemServerDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<WorkItemServerDbContext>();
        var connectionString = Environment.GetEnvironmentVariable("WORKITEM_DB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=IldWorkitems;Username=ild_workitems;Password=ild_workitems_password";
        optionsBuilder.UseNpgsql(connectionString, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", "public"));
        return new WorkItemServerDbContext(optionsBuilder.Options);
    }
}
