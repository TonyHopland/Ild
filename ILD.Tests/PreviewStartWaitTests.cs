using ILD.Core.Services.RunHosts.Local;
using System.Runtime.Versioning;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The background start (the start request's <c>wait: false</c>, see <see cref="WorkItemPreviewTests"/>)
/// still refuses a missing config or an unknown profile at once instead of failing later in the background.
/// </summary>
[SupportedOSPlatform("linux")]
public class PreviewStartWaitTests
{
    [Fact]
    public async Task A_background_start_of_a_missing_config_or_unknown_profile_is_refused_at_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "ild-start-wait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var service = new WorktreePreviewService(
                new Mock<IHttpClientFactory>().Object, new ConfigurationBuilder().Build(), PreviewProxyBase.Disabled,
                NullLogger<WorktreePreviewService>.Instance, LocalRunHost.Shared);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.BeginStartAsync(root, null, TestContext.Current.CancellationToken));

            File.WriteAllText(Path.Combine(root, "ild.config.json"),
                """{"preview":{"defaultProfile":"app","profiles":{"app":{"services":[{"name":"web","port":"web","command":"exit 0","healthUrl":"http://127.0.0.1:${PORT}/"}]}}}}""");
            var unknown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.BeginStartAsync(root, new WorktreePreviewStartOptions(ProfileName: "nope"), TestContext.Current.CancellationToken));
            Assert.Contains("nope", unknown.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
