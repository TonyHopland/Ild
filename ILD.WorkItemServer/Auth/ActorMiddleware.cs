using System.Text.Json;
using ILD.WorkItemServer.Domain;

namespace ILD.WorkItemServer.Auth;

/// <summary>
/// Requires every request that can change a work item — any POST, PUT or DELETE,
/// and the poll, whose heartbeat writes — to name who it acts for in X-ILD-Actor,
/// and hands that actor to the request's DbContext for the history it writes.
/// A request without a usable one is refused before it writes anything: an
/// unattributed history row would defeat the point of keeping history.
/// </summary>
public sealed class ActorMiddleware
{
    public const string HeaderName = "X-ILD-Actor";
    public const int MaxUsernameLength = 256;
    public const int MaxDetailLength = 1024;
    public const int MaxWorkItemIdLength = 64;

    private readonly RequestDelegate _next;

    public ActorMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!NeedsActor(ctx.Request))
        {
            await _next(ctx);
            return;
        }

        var (actor, error) = Parse(ctx.Request.Headers[HeaderName].ToString());
        if (actor is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error });
            return;
        }

        ctx.RequestServices.GetRequiredService<WorkItemServerDbContext>().HistoryActor = actor;
        await _next(ctx);
    }

    private static bool NeedsActor(HttpRequest request)
        => !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || request.Path.Equals("/workitems/poll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The header is base64 of UTF-8 JSON <c>{ username, kind, detail?, workItemId? }</c>,
    /// so any username or detail travels intact through a header.
    /// </summary>
    private static (WorkItemActor? Actor, string? Error) Parse(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return (null, $"Name the actor of this change in the {HeaderName} header.");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(header.Trim()));
            root = doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (null, $"{HeaderName} must be base64 of a UTF-8 JSON object.");
        }
        if (root.ValueKind != JsonValueKind.Object)
            return (null, $"{HeaderName} must be base64 of a UTF-8 JSON object.");

        if (!TryReadString(root, "username", out var username) || !TryReadString(root, "kind", out var kindName)
            || !TryReadString(root, "detail", out var detail) || !TryReadString(root, "workItemId", out var workItemId))
            return (null, $"Every {HeaderName} field must be a string.");

        if (string.IsNullOrWhiteSpace(username))
            return (null, $"{HeaderName} needs a username.");
        if (username.Length > MaxUsernameLength)
            return (null, $"An actor's username may be at most {MaxUsernameLength} characters.");
        if (kindName is null || !Enum.GetNames<WorkItemActorKind>().Contains(kindName, StringComparer.Ordinal))
            return (null, $"An actor's kind must be one of {string.Join(", ", Enum.GetNames<WorkItemActorKind>())}.");
        var kind = Enum.Parse<WorkItemActorKind>(kindName);
        if (kind != WorkItemActorKind.Human && string.IsNullOrWhiteSpace(detail))
            return (null, $"An actor of kind {kind} needs a detail naming what acted.");
        if (detail?.Length > MaxDetailLength)
            return (null, $"An actor's detail may be at most {MaxDetailLength} characters.");
        if (workItemId?.Length > MaxWorkItemIdLength)
            return (null, $"An actor's work item id may be at most {MaxWorkItemIdLength} characters.");

        return (new WorkItemActor(username, kind,
            string.IsNullOrWhiteSpace(detail) ? null : detail,
            string.IsNullOrWhiteSpace(workItemId) ? null : workItemId), null);
    }

    /// <summary>A missing or null property reads as null; any other non-string is refused.</summary>
    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return true;
    }
}
