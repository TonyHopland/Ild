using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// The signed-in user's own UI preferences, such as which taskboard columns are
/// folded, so they follow the user from one device to the next. Only ever the
/// caller's: there is no way to name another user. Keys and values are the UI's
/// to define; the server checks their shape and size and nothing else.
/// </summary>
[ApiController]
[Route("api/v1/preferences")]
public partial class PreferencesController : ControllerBase
{
    public const int MaxValueLength = 4096;

    private readonly IUserPreferenceStore _preferences;
    private readonly IAuthStore _auth;

    public PreferencesController(IUserPreferenceStore preferences, IAuthStore auth)
    {
        _preferences = preferences;
        _auth = auth;
    }

    public sealed class UpdatePreferenceRequest
    {
        [Required(AllowEmptyStrings = true)]
        public string? Value { get; set; }
    }

    // Lower-case words joined by dots, dashes or underscores: room for names like
    // "taskboard.folded", none for anything else.
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex KeyShape();

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var userId = await CallerIdAsync();
        if (userId is null) return Unauthorized();
        return Ok(await _preferences.GetAllAsync(userId.Value, ct));
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Put(string key, [FromBody] UpdatePreferenceRequest request, CancellationToken ct)
    {
        var userId = await CallerIdAsync();
        if (userId is null) return Unauthorized();
        if (!KeyShape().IsMatch(key))
            return BadRequest(new { error = "A preference key is up to 64 lower-case letters, digits, dots, dashes or underscores." });
        if (request.Value is not { } value) return BadRequest(new { error = "A value is required." });
        if (value.Length > MaxValueLength)
            return BadRequest(new { error = $"A preference value is at most {MaxValueLength} characters." });

        await _preferences.UpsertAsync(userId.Value, key, value, ct);
        return Ok(new { key, value });
    }

    private async Task<Guid?> CallerIdAsync()
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return null;
        return (await _auth.GetByUsernameAsync(username))?.Id;
    }
}
