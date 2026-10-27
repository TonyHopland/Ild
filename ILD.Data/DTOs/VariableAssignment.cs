namespace ILD.Data.DTOs;

/// <summary>One loop variable to write, with its value.</summary>
public sealed record VariableAssignment(string Name, string Value);
