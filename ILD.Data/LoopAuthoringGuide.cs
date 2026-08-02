namespace ILD.Data;

/// <summary>
/// A compact primer on the loop template model, so an agent can author a valid
/// <c>ild-loop-template/v1</c> document. Mirrors the node/edge vocabulary in
/// CONTEXT.md and the config fields the editor reads/writes.
///
/// <para>
/// It lives here, beside <see cref="ToolDescriptors"/>, because it has two
/// consumers and must not drift between them: the chat's Chat Context delivers it
/// once per agent session when a Loop Editor is open (ADR-0011), and the guide tool
/// serves the same bytes on demand for the rest of that session — named
/// <c>get_loop_authoring_guide</c> on the MCP surface and
/// <c>ild_get_loop_authoring_guide</c> on the Pi one, as every ILD tool is.
/// The pull path is what makes the once-per-session push safe —
/// an agent that has lost the guide from effective context can fetch it back
/// instead of going without, so the guidance stays reachable without being re-sent
/// on every turn.
/// </para>
/// </summary>
public static class LoopAuthoringGuide
{
    public const string Text =
        """
        Loop authoring guide — a loop is a directed graph executed from its Start node.
        Document shape: { "$schema": "ild-loop-template/v1", "name", "description", "recoveryPolicy" (AutoResume|NeedsReview|Cancel), "nodes": [...], "edges": [...] }.
        Each node: { "id", "type", "label" (unique), "config": {...} }. Each edge: { "id", "sourceNodeId", "targetNodeId", "edgeType" (OnSuccess|OnFailure|Custom), "name" (Custom only) }.
        Node types and their key config fields:
        - Start: entry point; creates the worktree/branch. config.createWorktree (bool), config.runInstall (bool).
        - Cmd: runs a shell command in the worktree, succeeds on exit 0. config.command.
        - AI: runs the agent. config.prompt, config.aiProviderId, config.toolAllowlist (string[]), config.matchRules ([{ "pattern", "edgeName" }] routing the AI output to Custom edges by name; no match takes OnSuccess).
        - Human: pauses for human input (becomes {{PreviousNode.Output}}). config.inputLabel, config.prompt, config.customEdges (string[] of Custom edge names this node may emit).
        - Prompt: renders a templated string as its Output (compose a downstream AI prompt). config.prompt. Always routes OnSuccess.
        - PR: opens/maintains a pull request. config.prDescriptionTemplate, config.prCommentTemplate, config.customEdges. Reserved PR edges: on_rejected, on_merge_conflict, on_ci_failed, on_approved, on_ci_passed, on_merged, on_abandoned — wire on_merged/on_abandoned to reach a terminal path.
        - Condition: a switch. config.cases ([{ "variant" (TextMatches|PrExists|HasTag), "subject"+"pattern" for TextMatches, "tag" for HasTag, "edgeName" }]), config.defaultEdge (the Custom edge taken when no case matches), config.output (the pass-through).
        - Cleanup: terminal sink (incoming edges only); marks the run finished.
        Field semantics you cannot infer from the name:
        - id: yours to choose, and only internal consistency matters — saving mints fresh GUIDs and remaps every reference. An edge whose sourceNodeId or targetNodeId names no node in the same document is silently dropped, with no error and no validation failure. After any structural edit, re-read the document and confirm each edge you added is still there.
        - aiProviderId: omit it unless a GUID was handed to you — you have no way to list providers. Unset or non-GUID falls back to the default provider; a GUID that no longer exists fails the run outright.
        - toolAllowlist: exactly four keys exist — "read", "write", "execute", "ild" — and only opencode/pi/claude-code providers honour them. Unknown keys are filtered out, and an empty, omitted or fully-filtered list means the PROVIDER DEFAULTS, not "no tools"; you cannot express "no tools" here.
        - Condition subject and output: template strings rendered through the placeholder pipeline before matching, both defaulting to {{Node.Input}}.
        - Precedence, and they differ: for AI matchRules the rule matching LAST in the output wins, so a closing verdict beats a word mentioned earlier; for Condition cases the FIRST matching case wins. Both match case-insensitively with no other regex options, each against a whole string — the AI node's output, or the Condition case's rendered subject — so ^ and $ bind to that whole string rather than to a line, and . does not cross newlines; write (?m) or (?s) inline when you need otherwise.
        - Only AI matchRules are pattern-checked at save (invalid, zero-width — x*, \b, (?=...) — and catastrophically slow patterns are rejected). A Condition case pattern is only compile-checked, so a zero-width one saves cleanly and then matches everything: being the first case, it wins forever and no later case or defaultEdge is ever reached. Make every Condition pattern require real characters yourself.
        Graph rules enforced when the human saves — a rejected save costs a whole round trip, so check them before you hand back:
        - A Start node and a Cleanup node must exist, and at least one path must run Start → Cleanup.
        - EVERY node must be reachable from Start. This is the rule a hand-written document fails most often: a node you added but never wired in rejects the entire save, not just itself.
        - Per source node: at most one OnSuccess and one OnFailure edge. Custom edges only on Human/AI/PR/Condition, each with a non-empty name unique within that node. Cleanup takes no outgoing edges.
        - A Condition needs ≥1 case and a defaultEdge, must have NO OnSuccess edge, and its case/default edge names must match its wired Custom edges exactly, both ways.
        - An AI node's matchRules and its Custom edges must likewise match both ways: a Custom edge no rule routes to, or a rule naming an edge that is not wired, rejects the save. Add the rule and the edge together, always.
        Edges: give every non-terminal node an OnSuccess edge. Use OnFailure edges sparingly — only when you genuinely want a distinct recovery path (e.g. an AI fix/retry loop). Most failures are transient (an AI node out of tokens or a throttled provider, a flaky command); a node that fails with NO OnFailure edge fails in place and parks the run for human feedback, so a human can fix and restart that node. That is almost always better than wiring OnFailure to Cleanup, which discards the run on a hiccup — do not route failures to Cleanup.
        Variables: templated fields expand placeholders — {{WorkItem.Title}}/{{WorkItem.Description}}, {{PreviousNode.Output}} (also spelled {{Node.Input}}), {{EventLog.LastN}}, and {{Var.<name>}}. A field is expanded exactly once, by the node that owns it; text a placeholder pulls in is never scanned again, so a prompt, a description or a variable value may quote the grammar safely. A Loop Variable is a mutable per-run string an AI node writes (via the agent variable API/tools) for a later node to read; names match [A-Za-z][A-Za-z0-9_]*.
        Sessions: config.useSession=true turns session handling on and REQUIRES a non-empty config.sessionPlaceholder="<name>" — useSession with only a forkFromPlaceholder is rejected at save. The placeholder names the session this node binds to and resumes on a later visit (use it when the node continues its own earlier work). Adding config.forkFromPlaceholder="<name>" re-copies that other placeholder's session into this one on every visit (use it when a branch needs that context without writing back into it). Without useSession every visit starts fresh — prefer that for work that does not depend on an earlier conversation, since it is cheaper and cannot inherit a stale plan.
        Per-iteration sessions: both session fields are templated, but on a NARROWER grammar than a prompt — {{Var.<name>}} only, and any other placeholder ({{PreviousNode.Output}}, {{WorkItem.Title}}, …) is rejected at save. Write config.sessionPlaceholder="ticket_{{Var.current_ticket}}" when a node inside a cycle needs memory WITHIN one item but not ACROSS items: each distinct value of the variable is a separate session, and a repeated value resumes the one it named before. A literal name behaves exactly as it always has. The strictness is at run time and deliberate: if the variable is unset when the node runs, or the whole field resolves to empty or to more than 128 characters, the NODE FAILS — an empty resolve would quietly drop every iteration back into one shared conversation, which is the bug this exists to prevent. So set the variable on a node that is guaranteed to run before this one.
        Authoring practices:
        - Keep an AI node's config.prompt down to a single placeholder, e.g. {{PreviousNode.Output}}, and put the full brief in an upstream Prompt node. The AI node re-renders and re-sends config.prompt on EVERY visit, so a brief inlined there is paid again on each retry pass around the loop.
        - Anchor matchRules on a verdict you instruct the agent to emit last, but anchor it with (?m)^TO_REVIEW$ — a bare ^TO_REVIEW$ requires the verdict to be the ENTIRE AI output, so an agent that explains itself first never matches it and falls through to OnSuccess instead. An unanchored token is the riskier fallback: it also matches a passing mention, and when no other rule matches later in the output that incidental hit is the one that routes.
        - Point config.defaultEdge somewhere useful — a terminal path or a human gate. Every output your cases did not anticipate lands there, so a token branch silently swallows the interesting cases.
        - Hand work across branches with {{Var.<name>}}, not {{PreviousNode.Output}}. Output is positional — only the node that just ran — while a loop variable is run-scoped and readable from any later node, whichever branch wrote it.
        """;
}
