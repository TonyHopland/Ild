using System.Security.Claims;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Tests;

/// <summary>Who tests act as when the WorkItem server needs an actor for its history.</summary>
internal static class TestActors
{
    public static readonly WorkItemActor Human = WorkItemActor.Human("tester");

    /// <summary>The same person as the WorkItem server records them, for tests that drive its services directly.</summary>
    public static readonly ILD.WorkItemServer.Domain.WorkItemActor ServerHuman =
        new("tester", ILD.WorkItemServer.Domain.WorkItemActorKind.Human, null, null);

    /// <summary>The instance's own user, which loop runs and background processes act as.</summary>
    public static readonly BootstrapCredentials Instance = new("instance-user", null);

    /// <summary>A controller built by hand, given the request of a signed-in user as the host would.</summary>
    public static T SignedIn<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester")], "test")),
            },
        };
        return controller;
    }
}
