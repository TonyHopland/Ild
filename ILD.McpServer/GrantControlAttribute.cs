namespace ILD.McpServer;

/// <summary>
/// Marks a tool class that only an external-agent grant at the Control level may use (ADR-0039). ILD's own run and chat
/// agents never get it, and an HTTP caller sees it listed only when its grant has that level.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GrantControlAttribute : Attribute;
