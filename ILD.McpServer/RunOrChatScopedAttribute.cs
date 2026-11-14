namespace ILD.McpServer;

/// <summary>
/// Marks a tool class whose tools act on the calling run or chat. An external-agent grant is neither,
/// so <see cref="ExternalAgentTools"/> leaves these out.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RunOrChatScopedAttribute : Attribute;
