using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

public class RemoteProvider : IHasUpdatedAt
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string Type { get; set; } = string.Empty;

    [Required]
    [MaxLength(1024)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? ApiKey { get; set; }

    [MaxLength(512)]
    public string? WebhookSecret { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>Optional standalone WorkItem server endpoint. When set, ILD
    /// polls this URL for work items associated with this provider's repositories.</summary>
    [MaxLength(1024)]
    public string? WorkItemServerUrl { get; set; }

    [MaxLength(1024)]
    public string? WorkItemApiKey { get; set; }

    /// <summary>Cron-style cadence for the regular poll. Stored as seconds for
    /// simplicity; a future iteration can promote this to a real cron string.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Faster cadence used while a local item is parked in HumanFeedback.</summary>
    public int GraceIntervalSeconds { get; set; } = 5;

    public int MaxConcurrentWorkItems { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [InverseProperty("RemoteProvider")]
    public ICollection<Repository> Repositories { get; set; } = new List<Repository>();
}
