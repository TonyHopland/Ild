using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class LinkPrRequest
{
    [Required]
    [Url]
    public string PrUrl { get; set; } = string.Empty;
}
