namespace ILD.Core.Services.Remote;

/// <summary>
/// Whether a failed call to the WorkItem Server means it could not be reached:
/// the connection or the HTTP exchange failed, or the client gave up waiting
/// (<see cref="HttpClient.Timeout"/>, a <see cref="TaskCanceledException"/>
/// over a <see cref="TimeoutException"/>). A cancellation the caller asked for,
/// such as an aborted request, is not this and must propagate as one.
/// </summary>
public static class WorkItemServerUnreachable
{
    public static bool Is(Exception ex)
        => ex is HttpRequestException or TaskCanceledException { InnerException: TimeoutException };
}
