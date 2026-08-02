using System.Net.Sockets;
using NPS.Core;
using NPS.Core.Exceptions;
using NPS.Core.Ncp;
using Xunit;

namespace NPS.Tests.Ncp;

/// <summary>NCP v0.10: native-mode session continuity across Anchor failover (NPS-CR-0009).</summary>
public sealed class NcpFailoverConnectorTests
{
    [Fact]
    public async Task Reresolves_and_reconnects_after_nid_mismatch()
    {
        var endpoints = new Queue<(string, int)>([("old-anchor", 17433), ("new-anchor", 17433)]);
        var connector = new NcpFailoverConnector<string>(
            resolveActive: _ => Task.FromResult(endpoints.Dequeue()),
            connect: (host, _, _) => host == "old-anchor"
                ? throw new NpsException(
                    "fenced",
                    NpsStatusCodes.ClientConflict,
                    NcpErrorCodes.NidMismatch)
                : Task.FromResult($"session@{host}"));

        Assert.Equal("session@new-anchor", await connector.ConnectAsync());
        Assert.Empty(endpoints); // both resolutions consumed
    }

    [Fact]
    public async Task Reresolves_after_socket_loss()
    {
        var resolves = 0;
        var connector = new NcpFailoverConnector<string>(
            resolveActive: _ => Task.FromResult(($"anchor-{++resolves}", 17433)),
            connect: (host, _, _) => host == "anchor-1"
                ? throw new SocketException((int)SocketError.ConnectionRefused)
                : Task.FromResult($"session@{host}"));

        Assert.Equal("session@anchor-2", await connector.ConnectAsync());
        Assert.Equal(2, resolves);
    }

    [Fact]
    public async Task Non_failover_errors_propagate_immediately()
    {
        var resolves = 0;
        var connector = new NcpFailoverConnector<string>(
            resolveActive: _ => Task.FromResult(($"anchor-{++resolves}", 17433)),
            connect: (_, _, _) => throw new NpsException(
                "bad frame",
                NpsStatusCodes.ClientBadFrame,
                NcpErrorCodes.FrameFlagsInvalid));

        await Assert.ThrowsAsync<NpsException>(() => connector.ConnectAsync());
        Assert.Equal(1, resolves); // no retry for non-failover failures
    }

    [Fact]
    public async Task Exhausted_attempts_rethrow_the_last_failure()
    {
        var connector = new NcpFailoverConnector<string>(
            resolveActive: _ => Task.FromResult(("anchor", 17433)),
            connect: (_, _, _) => throw new SocketException((int)SocketError.TimedOut),
            maxAttempts: 3);

        await Assert.ThrowsAsync<SocketException>(() => connector.ConnectAsync());
    }
}
