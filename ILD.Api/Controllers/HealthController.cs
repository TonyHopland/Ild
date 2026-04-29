using ILD.Data.Entities;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class HealthController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public HealthController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Check()
    {
        var health = new HealthResponse();

        try
        {
            await _dbContext.Database.CanConnectAsync();
            health.Database.Status = "healthy";
            health.Database.Message = "Connected";
        }
        catch (Exception ex)
        {
            health.Database.Status = "unhealthy";
            health.Database.Message = ex.Message;
        }

        try
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            if (Directory.Exists(dataDir))
            {
                var drive = new DriveInfo(Path.GetPathRoot(dataDir)!);
                var freeGb = drive.AvailableFreeSpace / (1024 * 1024 * 1024);
                health.DiskSpace.Status = freeGb < 1 ? "unhealthy" : "healthy";
                health.DiskSpace.Message = $"{freeGb:F1} GB free";
            }
            else
            {
                Directory.CreateDirectory(dataDir);
                health.DiskSpace.Status = "healthy";
                health.DiskSpace.Message = "Data directory created";
            }
        }
        catch (Exception ex)
        {
            health.DiskSpace.Status = "unhealthy";
            health.DiskSpace.Message = ex.Message;
        }

        health.Connectivity.Status = "healthy";
        health.Connectivity.Message = "OK";
        health.CheckedAt = DateTime.UtcNow;

        var allHealthy = health.Database.Status == "healthy" && health.DiskSpace.Status == "healthy";

        return allHealthy ? Ok(health) : StatusCode(StatusCodes.Status503ServiceUnavailable, health);
    }
}
