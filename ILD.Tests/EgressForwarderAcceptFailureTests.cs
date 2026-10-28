using System.Net.Sockets;
using ILD.Core.Services.Implementations.Network;

namespace ILD.Tests;

public sealed class EgressForwarderAcceptFailureTests
{
    /// <summary>
    /// Only the caller's own failures are worth another accept. Retrying the rest
    /// spins a core on a listener that has stopped working while the row goes on
    /// saying it is listening, so they retire it and let the next reconcile
    /// rebind.
    /// </summary>
    [Theory]
    [InlineData(SocketError.ConnectionReset, true)]
    [InlineData(SocketError.ConnectionAborted, true)]
    [InlineData(SocketError.Interrupted, true)]
    [InlineData(SocketError.TooManyOpenSockets, false)]
    [InlineData(SocketError.NotSocket, false)]
    [InlineData(SocketError.InvalidArgument, false)]
    [InlineData(SocketError.NoBufferSpaceAvailable, false)]
    public void An_accept_failure_is_retried_only_when_it_was_the_clients(SocketError error, bool retried)
    {
        Assert.Equal(retried, EgressForwarder.IsAbandonedHandshake(error));
    }
}
