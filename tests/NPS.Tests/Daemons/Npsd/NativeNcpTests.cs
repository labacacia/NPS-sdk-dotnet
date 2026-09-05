// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NPS.Core;
using NPS.Core.Caching;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;
using NPS.Daemon.Npsd;

namespace NPS.Tests.Daemons.Npsd;

public sealed class NativeNcpTests
{
    private static readonly NpsFrameCodec Codec = NpsFrameCodec.CreateDefault();

    [Fact]
    public async Task UnifiedPort_ServesHttpAndNativeNcpHandshake()
    {
        await using var fixture = await NativeFixture.StartAsync();

        using var http = new HttpClient { BaseAddress = fixture.BaseAddress };
        using var health = JsonDocument.Parse(await http.GetStringAsync("/health"));
        var native = health.RootElement.GetProperty("native_ncp");
        Assert.True(native.GetProperty("implemented").GetBoolean());
        Assert.True(native.GetProperty("shared_http_port").GetBoolean());
        Assert.Equal("NPS/1.0\n", native.GetProperty("preamble").GetString());
        Assert.False(native.GetProperty("tls_terminated_here").GetBoolean());
        Assert.False(native.GetProperty("public_ingress").GetBoolean());

        var client = new NcpNativeClient(Codec);
        await using var session = await client.ConnectAsync(
            "127.0.0.1",
            fixture.Port,
            MakeHello(["json"]));

        Assert.Equal(EncodingTier.Json, session.NegotiatedTier);
        Assert.Equal(["ncp"], session.ServerCaps.SupportedProtocols);
        Assert.Contains("anchor-cache", session.ServerCaps.Caps);
        Assert.EndsWith(":npsd", session.ServerCaps.NodeId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidNativePreamble_ClosesSilentlyWithinDeadline()
    {
        await using var fixture = await NativeFixture.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, fixture.Port);
        var stream = tcp.GetStream();

        var stopwatch = Stopwatch.StartNew();
        await stream.WriteAsync("NPS/9.9\n"u8.ToArray());
        await stream.FlushAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var oneByte = new byte[1];
        var count = await stream.ReadAsync(oneByte, timeout.Token);
        stopwatch.Stop();

        Assert.Equal(0, count);
        Assert.True(
            stopwatch.Elapsed < NcpPreamble.CloseDeadline,
            $"silent close took {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task IncompatibleHello_EmitsErrorAndCloses()
    {
        await using var fixture = await NativeFixture.StartAsync();
        var client = new NcpNativeClient(Codec);

        var exception = await Assert.ThrowsAsync<NcpHandshakeException>(() => client.ConnectAsync(
            "127.0.0.1",
            fixture.Port,
            MakeHello(["json"]) with { MinVersion = "9.0", NpsVersion = "9.0" }));

        Assert.Equal(NcpErrorCodes.VersionIncompatible, exception.ErrorCode);
    }

    [Fact]
    public async Task NonHelloFirstFrame_ClosesSilently()
    {
        await using var fixture = await NativeFixture.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, fixture.Port);
        var stream = tcp.GetStream();
        var schema = new FrameSchema { Fields = [new SchemaField("id", "string")] };
        var anchor = new AnchorFrame
        {
            AnchorId = AnchorFrameCache.ComputeAnchorId(schema),
            Schema = schema,
        };

        await stream.WriteAsync(NcpPreamble.ToArray());
        await stream.WriteAsync(Codec.Encode(anchor, EncodingTier.Json));
        await stream.FlushAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var oneByte = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(oneByte, timeout.Token));
    }

    [Fact]
    public async Task AnchorFrame_IsAcknowledgedAndCached()
    {
        await using var fixture = await NativeFixture.StartAsync();
        var client = new NcpNativeClient(Codec);
        var stopwatch = Stopwatch.StartNew();
        await using var session = await client.ConnectAsync(
            "127.0.0.1",
            fixture.Port,
            MakeHello(["json"]));

        var schema = new FrameSchema
        {
            Fields = [new SchemaField("id", "string")],
        };
        var anchorId = AnchorFrameCache.ComputeAnchorId(schema);
        var anchor = new AnchorFrame
        {
            AnchorId = anchorId,
            Schema = schema,
            Ttl = 60,
        };
        var stream = session.GetStream();
        await stream.WriteAsync(Codec.Encode(anchor, EncodingTier.Json));
        await stream.FlushAsync();

        var response = Codec.Decode(await ReadWireFrameAsync(stream));
        var ack = Assert.IsType<CapsFrame>(response);
        stopwatch.Stop();
        Assert.Equal(anchorId, ack.AnchorRef);
        Assert.Equal(0u, ack.Count);
        Assert.True(ack.Cached);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Hello + Anchor round trip took {stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        var cache = fixture.App.Services.GetRequiredService<AnchorFrameCache>();
        Assert.True(cache.TryGet(anchorId, out var cached));
        Assert.Equal(anchorId, cached.AnchorId);
    }

    [Fact]
    public async Task JsonOnlyServer_RejectsTier2OnlyHelloWithoutFallback()
    {
        await using var fixture = await NativeFixture.StartAsync(
            options => options with { NcpEnableMsgPack = false });
        var client = new NcpNativeClient(Codec);

        var exception = await Assert.ThrowsAsync<NcpHandshakeException>(() => client.ConnectAsync(
            "127.0.0.1",
            fixture.Port,
            MakeHello(["msgpack"])));

        Assert.Equal(NcpErrorCodes.EncodingUnsupported, exception.ErrorCode);
    }

    [Fact]
    public async Task AnchorWithMismatchedDigest_EmitsErrorAndCloses()
    {
        await using var fixture = await NativeFixture.StartAsync();
        var client = new NcpNativeClient(Codec);
        await using var session = await client.ConnectAsync(
            "127.0.0.1",
            fixture.Port,
            MakeHello(["json"]));
        var stream = session.GetStream();
        var invalid = new AnchorFrame
        {
            AnchorId = "sha256:" + new string('0', 64),
            Schema = new FrameSchema { Fields = [new SchemaField("id", "string")] },
            Ttl = 60,
        };

        await stream.WriteAsync(Codec.Encode(invalid, EncodingTier.Json));
        await stream.FlushAsync();
        var error = Assert.IsType<ErrorFrame>(Codec.Decode(await ReadWireFrameAsync(stream)));

        Assert.Equal(NpsStatusCodes.ClientConflict, error.Status);
        Assert.Equal(NcpErrorCodes.AnchorIdMismatch, error.Error);
        var oneByte = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(oneByte));
    }

    [Fact]
    public void DefaultBind_IsLoopbackUnifiedPort()
    {
        var options = new NpsdOptions();
        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(17433, options.Port);
    }

    private static HelloFrame MakeHello(IReadOnlyList<string> encodings) => new()
    {
        MinVersion = "0.1",
        NpsVersion = "0.11",
        SupportedEncodings = encodings,
        SupportedProtocols = ["ncp"],
        MaxFramePayload = FrameHeader.DefaultMaxPayload,
        MaxConcurrentStreams = 1,
    };

    private static async Task<byte[]> ReadWireFrameAsync(Stream stream)
    {
        var first = new byte[2];
        await stream.ReadExactlyAsync(first);
        var extended = ((FrameFlags)first[1] & FrameFlags.Ext) != 0;
        var headerSize = extended ? FrameHeader.ExtendedSize : FrameHeader.DefaultSize;
        var headerBytes = new byte[headerSize];
        first.CopyTo(headerBytes, 0);
        await stream.ReadExactlyAsync(headerBytes.AsMemory(2));
        var header = FrameHeader.Parse(headerBytes);
        var wire = new byte[headerSize + checked((int)header.PayloadLength)];
        headerBytes.CopyTo(wire, 0);
        await stream.ReadExactlyAsync(wire.AsMemory(headerSize));
        return wire;
    }

    private sealed class NativeFixture : IAsyncDisposable
    {
        internal int Port { get; }
        internal Uri BaseAddress => new($"http://127.0.0.1:{Port}");
        internal WebApplication App { get; }
        private string DataDir { get; }

        private NativeFixture(int port, string dataDir, WebApplication app)
        {
            Port = port;
            DataDir = dataDir;
            App = app;
        }

        internal static async Task<NativeFixture> StartAsync(
            Func<NpsdOptions, NpsdOptions>? configure = null)
        {
            var port = GetFreePort();
            var dataDir = Path.Combine(Path.GetTempPath(), $"npsd-native-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDir);
            var options = new NpsdOptions
            {
                Host = "127.0.0.1",
                Port = port,
                DataDir = dataDir,
                NcpPreambleTimeoutMs = 1_000,
                NcpHelloTimeoutMs = 1_000,
            };
            options = configure?.Invoke(options) ?? options;
            var app = NpsdHost.Build([], options);
            await app.StartAsync();
            return new NativeFixture(port, dataDir, app);
        }

        public async ValueTask DisposeAsync()
        {
            try { await App.StopAsync(); } catch { /* best-effort test cleanup */ }
            await App.DisposeAsync();
            try { Directory.Delete(DataDir, recursive: true); } catch { /* leave for diagnostics */ }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
