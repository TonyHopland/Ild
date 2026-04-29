using System.ComponentModel.DataAnnotations;

namespace ILD.Core.DTOs;

public class LinkPrRequest
{
    [Required]
    [Url]
    public string PrUrl { get; set; } = string.Empty;
}
