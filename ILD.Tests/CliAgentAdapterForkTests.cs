using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

/// <summary>
/// Unit coverage for the shared fork primitive every CLI adapter inherits from
/// <see cref="CliAgentAdapterBase"/> — see ADR-0009 (adapter feature parity).
/// </summary>
public class CliAgentAdapterForkTests
{
    [Fact]
    public void RewriteSessionTranscript_retargets_every_session_id_reference()
    {
        // Shape mirrors a claude wrapped snapshot: a top-level id plus per-event
        // references, all of which must point at the new session after a fork.
        var source =
            "{\"format\":\"claude-jsonl\",\"sessionId\":\"src-1\",\"events\":[" +
            "{\"session_id\":\"src-1\",\"text\":\"hello\"}," +
            "{\"session_id\":\"src-1\",\"text\":\"world\"}]}";

        var forked = CliAgentAdapterBase.RewriteSessionTranscript(source, "src-1", "dst-2");

        Assert.DoesNotContain("src-1", forked);
        Assert.Equal(3, forked.Split("dst-2").Length - 1);
        // Non-id content is preserved verbatim.
        Assert.Contains("\"text\":\"hello\"", forked);
        Assert.Contains("\"text\":\"world\"", forked);
    }

    [Fact]
    public void RewriteSessionTranscript_rewrites_multiline_jsonl_header()
    {
        // pi snapshots are raw multi-line JSONL; the textual rewrite must still
        // retarget the session header id so pi's header-id lookup matches.
        var source =
            "{\"type\":\"session\",\"id\":\"pi-src\",\"cwd\":\"/w\"}\n" +
            "{\"type\":\"message\",\"sessionId\":\"pi-src\"}\n";

        var forked = CliAgentAdapterBase.RewriteSessionTranscript(source, "pi-src", "pi-dst");

        Assert.DoesNotContain("pi-src", forked);
        Assert.Contains("\"id\":\"pi-dst\"", forked);
        Assert.Contains("\"sessionId\":\"pi-dst\"", forked);
    }

    [Fact]
    public void RewriteSessionTranscript_is_a_noop_for_blank_or_equal_ids()
    {
        const string source = "{\"sessionId\":\"keep\"}";

        Assert.Equal(source, CliAgentAdapterBase.RewriteSessionTranscript(source, "", "dst"));
        Assert.Equal(source, CliAgentAdapterBase.RewriteSessionTranscript(source, "keep", "keep"));
    }
}
