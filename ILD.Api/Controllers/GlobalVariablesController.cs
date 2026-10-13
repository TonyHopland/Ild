using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Controllers;

/// <summary>
/// The instance's global variables, which loop prompts read with
/// <c>{{Global.&lt;name&gt;}}</c>. Each comes with the live loop templates whose
/// latest version uses it. User-only, like everything outside the agent API.
/// </summary>
[ApiController]
[Route("api/v1/global-variables")]
public class GlobalVariablesController : ControllerBase
{
    private readonly IGlobalVariableStore _variables;
    private readonly ILoopTemplateStore _templates;
    private readonly ISchedulerSettingsService _settings;

    public GlobalVariablesController(IGlobalVariableStore variables, ILoopTemplateStore templates, ISchedulerSettingsService settings)
    {
        _variables = variables;
        _templates = templates;
        _settings = settings;
    }

    public sealed class CreateVariableRequest
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
    }

    /// <summary>The name is the variable's identity (templates use it by name), so an update cannot change it.</summary>
    public sealed class UpdateVariableRequest
    {
        public string? Value { get; set; }
    }

    private sealed record TemplateRef(Guid Id, string Name);

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var usage = await UsageAsync();
        return Ok((await _variables.GetAllAsync(ct)).Select(v => View(v, usage)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateVariableRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return BadRequest(new { error = "Give the variable a name, e.g. review_rules" });
        if (!PromptPlaceholderRegistry.IsValidVariableName(name))
            return BadRequest(new { error = "A name is a letter followed by up to 127 letters, digits or underscores" });
        if (await ValueProblemAsync(request.Value, ct) is { } problem)
            return BadRequest(new { error = problem });

        var normalized = GlobalVariable.Normalize(name);
        if ((await _variables.GetAllAsync(ct)).Any(v => v.NormalizedName == normalized))
            return BadRequest(new { error = NameTaken(name) });

        var variable = new GlobalVariable
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = normalized,
            Value = request.Value!,
            UpdatedAt = DateTime.UtcNow,
        };
        try
        {
            await _variables.AddAsync(variable, ct);
        }
        catch (DbUpdateException)
        {
            // The unique index caught what the read above could not: two saves racing.
            return BadRequest(new { error = NameTaken(name) });
        }
        return CreatedAtAction(nameof(GetAll), View(variable, await UsageAsync()));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateVariableRequest request, CancellationToken ct)
    {
        var variable = await _variables.GetAsync(id, ct);
        if (variable is null) return NotFound(new { error = Gone });
        if (await ValueProblemAsync(request.Value, ct) is { } problem)
            return BadRequest(new { error = problem });

        variable.Value = request.Value!;
        variable.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _variables.UpdateAsync(variable, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted between the read above and this save.
            return NotFound(new { error = Gone });
        }
        return Ok(View(variable, await UsageAsync()));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => await _variables.DeleteAsync(id, ct) ? NoContent() : NotFound(new { error = Gone });

    /// <summary>
    /// The limit is checked on every write, never on what is stored, so lowering
    /// it leaves a longer value alone until its next edit.
    /// </summary>
    private async Task<string?> ValueProblemAsync(string? value, CancellationToken ct)
    {
        if (value is null) return "Give the variable a value; it may be empty";
        var limit = await _settings.GetGlobalVariableMaxLengthAsync(ct);
        return value.Length > limit ? $"The value is {value.Length} characters; the limit is {limit}." : null;
    }

    /// <summary>
    /// The live templates using each global variable, keyed by its normalized
    /// name: those whose latest version has a node config naming it.
    /// </summary>
    private async Task<ILookup<string, TemplateRef>> UsageAsync()
    {
        // One read of every live template: names are not unique, so paging on
        // the name ordering could skip or repeat templates tied across a page edge.
        var uses = new List<(string Name, TemplateRef Template)>();
        foreach (var template in await _templates.GetAllAsync(take: int.MaxValue))
        {
            if (await _templates.GetLatestVersionAsync(template.Id) is not { } latest) continue;
            var names = (await _templates.GetNodesForVersionAsync(latest.Id))
                .SelectMany(n => PromptPlaceholderRegistry.GlobalVariableNames(n.Config))
                .Select(GlobalVariable.Normalize)
                .Distinct();
            uses.AddRange(names.Select(n => (n, new TemplateRef(template.Id, template.Name))));
        }
        return uses.ToLookup(u => u.Name, u => u.Template, StringComparer.Ordinal);
    }

    private const string Gone = "No such variable; it may have been deleted.";

    private static string NameTaken(string name) => $"A variable named '{name}' already exists";

    private static object View(GlobalVariable v, ILookup<string, TemplateRef> usage) => new
    {
        id = v.Id,
        name = v.Name,
        value = v.Value,
        updatedAt = v.UpdatedAt,
        usedBy = usage[v.NormalizedName].Select(t => new { id = t.Id, name = t.Name }),
    };
}
