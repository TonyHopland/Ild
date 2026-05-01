using System.ComponentModel.DataAnnotations;
using FluentAssertions;
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
        Validate(dto).Should().NotBeEmpty();
    }

    [Fact]
    public void RemoteProviderDto_missing_required_fields_produces_errors()
    {
        var dto = new RemoteProviderDto();
        Validate(dto).Should().NotBeEmpty();
    }

    [Fact]
    public void LoopTemplateCreateRequest_empty_name_produces_error()
    {
        var dto = new LoopTemplateCreateRequest { Name = "" };
        Validate(dto).Should().Contain(r => r.MemberNames.Contains(nameof(LoopTemplateCreateRequest.Name)));
    }

    [Fact]
    public void WorkItemTransitionRequest_empty_target_produces_error()
    {
        var dto = new WorkItemTransitionRequest();
        Validate(dto).Should().Contain(r => r.MemberNames.Contains(nameof(WorkItemTransitionRequest.TargetStatus)));
    }

    [Fact]
    public void LoginRequest_missing_credentials_produces_errors()
    {
        var dto = new LoginRequest();
        Validate(dto).Should().NotBeEmpty();
    }

    [Fact]
    public void LinkPrRequest_invalid_url_produces_error()
    {
        var dto = new LinkPrRequest { PrUrl = "not-a-url" };
        Validate(dto).Should().Contain(r => r.MemberNames.Contains(nameof(LinkPrRequest.PrUrl)));
    }

    [Fact]
    public void AddDependencyRequest_empty_id_produces_error()
    {
        var dto = new AddDependencyRequest();
        Validate(dto).Should().NotBeEmpty();
    }

    [Fact]
    public void HumanFeedbackInputRequest_empty_input_produces_error()
    {
        var dto = new HumanFeedbackInputRequest();
        Validate(dto).Should().NotBeEmpty();
    }

    [Fact]
    public void WebhookPayload_missing_required_fields_produces_errors()
    {
        var dto = new WebhookPayload(EventType: "", RepositoryId: "", PullRequestId: null, PullRequestUrl: null, Comment: null, MergeStatus: null);
        Validate(dto).Should().NotBeEmpty();
    }
}
