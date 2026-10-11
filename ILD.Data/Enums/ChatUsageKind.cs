namespace ILD.Data.Enums;

/// <summary>Which model call of a chat a <c>ChatUsage</c> records.</summary>
public enum ChatUsageKind
{
    /// <summary>A chat turn's reply.</summary>
    Turn = 0,

    /// <summary>The call that generated the chat's title.</summary>
    Title = 1,
}
