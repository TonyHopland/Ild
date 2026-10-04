namespace ILD.Data.Enums;

/// <summary>
/// Where a chat's title came from, so a title someone chose is never replaced by
/// one ILD made up.
/// </summary>
public enum ChatTitleSource
{
    /// <summary>The cleaned-up start of the first message, or no title yet.</summary>
    Fallback = 0,

    /// <summary>Summarised by the title provider after the first exchange.</summary>
    Auto = 1,

    /// <summary>Renamed by the user; nothing automatic touches it again.</summary>
    Manual = 2,
}
