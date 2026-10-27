namespace ILD.Data.Enums;

/// <summary>What one write did to a loop variable.</summary>
public enum VariableWriteEffect
{
    /// <summary>The variable did not exist; the write created it.</summary>
    Created,

    /// <summary>The write replaced a different value.</summary>
    Changed,

    /// <summary>The write stored the value the variable already had.</summary>
    Unchanged,
}
