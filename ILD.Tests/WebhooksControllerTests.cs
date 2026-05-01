using System.Text;
using System.Text.Json;
using FluentAssertions;
using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

public class WebhooksControllerTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly AppDbContext _db;

    public WebhooksControllerTests()
    {
        _conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        _db = new AppDbContext(opts);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static string Hmac(string secret, string body)
    {
        using var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    private static (WebhooksController controller, Mock<IPrSyncService> prSync, string body) BuildRequest(
        AppDbContext db, string secret, string? signature, WebhookPayload payload)
    {
        var prSync = new Mock<IPrSyncService>();
        var controller = new WebhooksController(prSync.Object, db);
        var ctx = new DefaultHttpContext();
        var bodyJson = JsonSerializer.Serialize(payload);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(bodyJson));
        ctx.Request.ContentType = "application/json";
        if (signature != null) ctx.Request.Headers["X-Forgejo-Signature"] = signature;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, prSync, bodyJson);
    }

    [Fact]
    public async Task Forgejo_returns_unauthorized_when_signature_missing()
    {
        _db.RemoteProviders.Add(new RemoteProvider { Id = Guid.NewGuid(), Name = "p", Type = "Forgejo", Url = "x", WebhookSecret = "topsecret" });
        await _db.SaveChangesAsync();

        var (c, prSync, _) = BuildRequest(_db, "topsecret", null,
            new WebhookPayload("pull_request.merged", "r1", "1", "https://x/pr/1", null, "merged"));
        var result = await c.Forgejo();

        result.Should().BeOfType<UnauthorizedResult>();
        prSync.Verify(s => s.HandleWebhookAsync(It.IsAny<WebhookPayload>()), Times.Never);
    }

    [Fact]
    public async Task Forgejo_returns_unauthorized_when_signature_wrong()
    {
        _db.RemoteProviders.Add(new RemoteProvider { Id = Guid.NewGuid(), Name = "p", Type = "Forgejo", Url = "x", WebhookSecret = "topsecret" });
        await _db.SaveChangesAsync();

        var (c, prSync, _) = BuildRequest(_db, "topsecret", "deadbeef",
            new WebhookPayload("pull_request.merged", "r1", "1", "https://x/pr/1", null, "merged"));
        var result = await c.Forgejo();

        result.Should().BeOfType<UnauthorizedResult>();
        prSync.Verify(s => s.HandleWebhookAsync(It.IsAny<WebhookPayload>()), Times.Never);
    }

    [Fact]
    public async Task Forgejo_accepts_request_when_signature_matches_provider_secret()
    {
        const string secret = "topsecret";
        _db.RemoteProviders.Add(new RemoteProvider { Id = Guid.NewGuid(), Name = "p", Type = "Forgejo", Url = "x", WebhookSecret = secret });
        await _db.SaveChangesAsync();

        var payload = new WebhookPayload("pull_request.merged", "r1", "1", "https://x/pr/1", null, "merged");
        var bodyJson = JsonSerializer.Serialize(payload);
        var sig = Hmac(secret, bodyJson);

        var (c, prSync, _) = BuildRequest(_db, secret, sig, payload);
        var result = await c.Forgejo();

        result.Should().BeOfType<OkResult>();
        prSync.Verify(s => s.HandleWebhookAsync(It.IsAny<WebhookPayload>()), Times.Once);
    }
}
