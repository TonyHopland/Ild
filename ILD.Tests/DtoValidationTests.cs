using System.ComponentModel.DataAnnotations;
using ILD.Api.Controllers;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// Validation tests for the write DTOs whose rules are ILD's own (a URL shape, a size cap),
/// not the framework's [Required] handling.
/// </summary>
public class DtoValidationTests
{
    private static List<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void LinkPrRequest_invalid_url_produces_error()
    {
        var dto = new LinkPrRequest { PrUrl = "not-a-url" };
        Assert.Contains(Validate(dto), r => r.MemberNames.Contains(nameof(LinkPrRequest.PrUrl)));
    }

    [Fact]
    public void HumanFeedbackInputRequest_overlong_input_produces_error()
    {
        var dto = new HumanFeedbackInputRequest { Input = new string('x', 8193) };
        Assert.NotEmpty(Validate(dto));
    }
}
