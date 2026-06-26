// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using NPS.Core;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;
using NPS.Core.Registry;

namespace NPS.Tests.Ncp;

/// <summary>
/// Integration tests for <see cref="NcpNativeClient"/> and <see cref="NcpServer"/>
/// using an in-process TCP loopback connection.
/// </summary>
public sealed class NcpNativeModeTests
{
    private static NpsFrameCodec MakeCodec()
    {
        var registry = FrameRegistry.CreateDefault();
        return new NpsFrameCodec(new Tier1JsonCodec(), new Tier2MsgPackCodec(), registry);
    }

    private static HelloFrame MakeHello(string npsVersion = "0.7") => new()
    {
        NpsVersion          = npsVersion,
        MinVersion          = "0.6",
        SupportedEncodings  = ["msgpack", "json"],
        SupportedProtocols  = ["ncp", "nwp"],
    };

    private static NcpHandshakeCapsFrame MakeCaps() => new()
    {
        NodeId = "urn:nps:agent:test.local:server1",
        Caps   = ["ncp", "nwp", "nip"],
    };

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handshake_Succeeds_ClientReceivesServerCaps()
    {
        using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var codec = MakeCodec();
        int port  = GetFreePort();

        await using var server = new NcpServer(port, codec);
        server.Start();

        // Server: accept and reply
        var serverTask = Task.Run(async () =>
        {
            var conn    = await server.AcceptConnectionAsync(cts.Token);
            Assert.Equal("0.7", conn.ClientHello.NpsVersion);
            Assert.Contains("msgpack", conn.ClientHello.SupportedEncodings);
            await using var _ = await conn.AcceptAsync(MakeCaps(), cts.Token);
        }, cts.Token);

        // Client: connect and verify caps
        var client = new NcpNativeClient(codec);
        await using var session = await client.ConnectAsync("127.0.0.1", port, MakeHello(), cts.Token);

        Assert.Equal("urn:nps:agent:test.local:server1", session.ServerCaps.NodeId);
        Assert.Contains("nwp", session.ServerCaps.Caps);
        Assert.Equal(EncodingTier.MsgPack, session.NegotiatedTier);  // server picks msgpack (first preference)

        await serverTask;
    }

    // ── Server rejects ────────────────────────────────────────────────────────

    [Fact]
    public async Task Handshake_ServerRejects_ClientThrowsNcpHandshakeException()
    {
        using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var codec = MakeCodec();
        int port  = GetFreePort();

        await using var server = new NcpServer(port, codec);
        server.Start();

        var serverTask = Task.Run(async () =>
        {
            var conn = await server.AcceptConnectionAsync(cts.Token);
            await conn.RejectAsync(new ErrorFrame
            {
                Status  = NpsStatusCodes.ProtoVersionIncompatible,
                Error   = NcpErrorCodes.VersionIncompatible,
                Message = "Server requires NPS >= 0.8.",
            }, cts.Token);
        }, cts.Token);

        var client = new NcpNativeClient(codec);
        var ex = await Assert.ThrowsAsync<NcpHandshakeException>(
            () => client.ConnectAsync("127.0.0.1", port, MakeHello(), cts.Token));

        Assert.Equal(NcpErrorCodes.VersionIncompatible, ex.ErrorCode);

        await serverTask;
    }

    // ── Invalid preamble ──────────────────────────────────────────────────────

    [Fact]
    public async Task Server_InvalidPreamble_ThrowsNcpPreambleInvalidException()
    {
        using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var codec = MakeCodec();
        int port  = GetFreePort();

        await using var server = new NcpServer(port, codec);
        server.Start();

        var serverTask = Task.Run(async () =>
        {
            await Assert.ThrowsAsync<NcpPreambleInvalidException>(
                () => server.AcceptConnectionAsync(cts.Token));
        }, cts.Token);

        // Rogue client — writes garbage preamble
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
        var stream = tcp.GetStream();
        await stream.WriteAsync("GARBAGE!\n"u8.ToArray(), cts.Token);
        await stream.FlushAsync(cts.Token);

        await serverTask;
    }

    // ── JSON-only client ──────────────────────────────────────────────────────

    [Fact]
    public async Task Handshake_JsonOnlyClient_NegotiatesJson()
    {
        using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var codec = MakeCodec();
        int port  = GetFreePort();

        await using var server = new NcpServer(port, codec);
        server.Start();

        var serverTask = Task.Run(async () =>
        {
            var conn = await server.AcceptConnectionAsync(cts.Token);
            await using var _ = await conn.AcceptAsync(MakeCaps(), cts.Token);
        }, cts.Token);

        // Client that only speaks JSON
        var jsonOnlyHello = new HelloFrame
        {
            NpsVersion          = "0.7",
            SupportedEncodings  = ["json"],
            SupportedProtocols  = ["ncp"],
        };
        var client = new NcpNativeClient(codec);
        await using var session = await client.ConnectAsync("127.0.0.1", port, jsonOnlyHello, cts.Token);

        Assert.Equal(EncodingTier.Json, session.NegotiatedTier);

        await serverTask;
    }

    [Fact]
    public async Task Server_AuthenticateStreamHook_RunsBeforeHandshake()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var codec = MakeCodec();
        int port = GetFreePort();
        var hookCalled = false;

        await using var server = new NcpServer(port, codec, new NcpServerOptions
        {
            AuthenticateStreamAsync = (_, stream, _) =>
            {
                hookCalled = true;
                return ValueTask.FromResult(stream);
            },
        });
        server.Start();

        var serverTask = Task.Run(async () =>
        {
            var conn = await server.AcceptConnectionAsync(cts.Token);
            await using var _ = await conn.AcceptAsync(MakeCaps(), cts.Token);
        }, cts.Token);

        var client = new NcpNativeClient(codec);
        await using var session = await client.ConnectAsync("127.0.0.1", port, MakeHello(), cts.Token);

        await serverTask;
        Assert.True(hookCalled);
    }

    [Fact]
    public async Task Server_RequireAuthenticatedStream_RejectsPlaintextHook()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var codec = MakeCodec();
        int port = GetFreePort();

        await using var server = new NcpServer(port, codec, new NcpServerOptions
        {
            RequireAuthenticatedStream = true,
            AuthenticateStreamAsync = (_, stream, _) => ValueTask.FromResult(stream),
        });
        server.Start();

        var serverTask = Task.Run(async () =>
        {
            await Assert.ThrowsAsync<NpsFrameException>(
                () => server.AcceptConnectionAsync(cts.Token));
        }, cts.Token);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, cts.Token);

        await serverTask;
    }

    [Fact]
    public async Task Server_HelloPayloadOverConfiguredLimit_ThrowsBeforeAllocatingPayload()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var codec = MakeCodec();
        int port = GetFreePort();

        await using var server = new NcpServer(port, codec, new NcpServerOptions
        {
            MaxHelloPayload = 16,
        });
        server.Start();

        var serverTask = Task.Run(async () =>
        {
            var ex = await Assert.ThrowsAsync<NpsFrameException>(
                () => server.AcceptConnectionAsync(cts.Token));
            Assert.Contains("HelloFrame payload length", ex.Message);
        }, cts.Token);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
        var stream = tcp.GetStream();
        await NcpPreamble.WriteAsync(stream, cts.Token);
        var helloWire = codec.Encode(MakeHello(), EncodingTier.Json);
        await stream.WriteAsync(helloWire, cts.Token);
        await stream.FlushAsync(cts.Token);

        await serverTask;
    }
}
