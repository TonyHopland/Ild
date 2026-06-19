using System.ComponentModel.DataAnnotations;
using ILD.Api.Controllers;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// Negative-path validation tests for write DTOs. Each test instantiates a DTO with
/// missing required fields and asserts that DataAnnotations validation produces errors.
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
    public void RepositoryDto_missing_required_fields_produces_errors()
    {
        var dto = new RepositoryDto(); // all strings empty
        Assert.NotEmpty(Validate(dto));
    }

    [Fact]
    public void RemoteProviderDto_missing_required_fields_produces_errors()
    {
        var dto = new RemoteProviderDto();
        Assert.NotEmpty(Validate(dto));
    }

    [Fact]
    public void LoopTemplateCreateRequest_empty_name_produces_error()
    {
        var dto = new LoopTemplateCreateRequest { Name = "" };
        Assert.Contains(Validate(dto), r => r.MemberNames.Contains(nameof(LoopTemplateCreateRequest.Name)));
    }

    [Fact]
    public void WorkItemTransitionRequest_empty_target_produces_error()
    {
        var dto = new WorkItemTransitionRequest();
        Assert.Contains(Validate(dto), r => r.MemberNames.Contains(nameof(WorkItemTransitionRequest.TargetStatus)));
    }

    [Fact]
    public void LoginRequest_missing_credentials_produces_errors()
    {
        var dto = new LoginRequest();
        Assert.NotEmpty(Validate(dto));
    }

    [Fact]
    public void LinkPrRequest_invalid_url_produces_error()
    {
        var dto = new LinkPrRequest { PrUrl = "not-a-url" };
        Assert.Contains(Validate(dto), r => r.MemberNames.Contains(nameof(LinkPrRequest.PrUrl)));
    }

    [Fact]
    public void AddDependencyRequest_empty_id_produces_error()
    {
        var dto = new AddDependencyRequest();
        Assert.NotEmpty(Validate(dto));
    }

    [Fact]
    public void HumanFeedbackInputRequest_empty_input_is_valid()
    {
        var dto = new HumanFeedbackInputRequest();
        Assert.Empty(Validate(dto));
    }

    [Fact]
    public void HumanFeedbackInputRequest_overlong_input_produces_error()
    {
        var dto = new HumanFeedbackInputRequest { Input = new string('x', 8193) };
        Assert.NotEmpty(Validate(dto));
    }

    [Fact]
    public void WorkItemCreateRequest_long_description_is_valid()
    {
        // Description is unbounded; a body well past the old 4096/8192 caps must validate.
        var dto = new WorkItemCreateRequest { Title = "x", Description = new string('x', 20000) };
        Assert.DoesNotContain(Validate(dto), r => r.MemberNames.Contains(nameof(WorkItemCreateRequest.Description)));
    }

    [Fact]
    public void AgentWorkItemCreateRequest_long_description_is_valid()
    {
        var dto = new AgentWorkItemCreateRequest { Title = "x", Description = new string('x', 20000) };
        Assert.DoesNotContain(Validate(dto), r => r.MemberNames.Contains(nameof(AgentWorkItemCreateRequest.Description)));
    }

    [Fact]
    public void WebhookPayload_missing_required_fields_produces_errors()
    {
        var dto = new WebhookPayload(EventType: "", RepositoryId: "", PullRequestId: null, PullRequestUrl: null, Comment: null, MergeStatus: null);
        Assert.NotEmpty(Validate(dto));
    }
}
