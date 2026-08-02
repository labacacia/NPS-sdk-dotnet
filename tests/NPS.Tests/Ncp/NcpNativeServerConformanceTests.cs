// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Ncp;

namespace NPS.Tests.Ncp;

public sealed class NcpNativeServerConformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void SharedNativeServerVectors_Pass()
    {
        var path = FindRepoFile("spec/conformance/ncp/native_server_handshake_vectors.json");
        var suite = JsonSerializer.Deserialize<VectorSuite>(
            File.ReadAllText(path),
            JsonOptions);
        Assert.NotNull(suite);
        Assert.Equal(12, suite.Vectors.Count);

        foreach (var vector in suite.Vectors)
            AssertVector(vector);
    }

    private static void AssertVector(VectorCase vector)
    {
        var server = vector.Input.Server;
        var transport = vector.Input.Transport;
        var preamble = Convert.FromHexString(transport.PreambleHex);
        var preambleDecision = NcpNativeServerPolicy.EvaluatePreamble(
            preamble,
            TimeSpan.FromMilliseconds(transport.PreambleElapsedMs),
            TimeSpan.FromMilliseconds(server.PreambleTimeoutMs));

        if (preambleDecision.Action == NcpHandshakeAction.SilentClose)
        {
            AssertDecision(vector, preambleDecision);
            return;
        }

        Assert.NotNull(transport.FirstFrameType);
        var flags = transport.FirstFrameTier switch
        {
            "json" => FrameFlags.Tier1Json,
            "msgpack" => FrameFlags.Tier2MsgPack,
            "binary_vector.v1" => FrameFlags.Tier3BinaryVector,
            _ => (FrameFlags)0x03,
        };
        if (transport.FirstFrameEncrypted) flags |= FrameFlags.Encrypted;
        if (transport.FirstFrameExtended) flags |= FrameFlags.Ext;

        var header = new FrameHeader(
            (FrameType)Convert.ToByte(transport.FirstFrameType[2..], 16),
            flags,
            transport.HelloPayloadLength);
        var headerDecision = NcpNativeServerPolicy.EvaluateHelloHeader(
            header,
            TimeSpan.FromMilliseconds(transport.HelloElapsedMs),
            TimeSpan.FromMilliseconds(server.HelloTimeoutMs),
            server.MaxHelloPayload);
        if (headerDecision.Action == NcpHandshakeAction.SilentClose)
        {
            AssertDecision(vector, headerDecision);
            return;
        }

        Assert.NotNull(vector.Input.Hello);
        var hello = vector.Input.Hello;
        var negotiation = NcpNativeServerPolicy.Negotiate(
            new NcpHandshakeProfile
            {
                MinVersion = server.MinVersion!,
                NpsVersion = server.NpsVersion!,
                SupportedEncodings = server.SupportedEncodings!,
                SupportedProtocols = server.SupportedProtocols!,
                MaxFramePayload = server.MaxFramePayload,
                ExtSupport = server.ExtSupport,
                MaxConcurrentStreams = server.MaxConcurrentStreams,
            },
            new HelloFrame
            {
                MinVersion = hello.MinVersion,
                NpsVersion = hello.NpsVersion,
                SupportedEncodings = hello.SupportedEncodings,
                SupportedProtocols = hello.SupportedProtocols,
                MaxFramePayload = hello.MaxFramePayload,
                ExtSupport = hello.ExtSupport,
                MaxConcurrentStreams = hello.MaxConcurrentStreams,
            });
        AssertDecision(vector, negotiation);
    }

    private static void AssertDecision(
        VectorCase vector,
        NcpHandshakeDecision actual)
    {
        var expected = vector.Expected;
        Assert.Equal(ParseAction(expected.Action), actual.Action);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Error, actual.Error);
        Assert.Equal(expected.DiagnosticError, actual.DiagnosticError);
        Assert.Equal(expected.SessionVersion, actual.SessionVersion);
        Assert.Equal(expected.NegotiatedEncoding, actual.NegotiatedEncoding);
        Assert.Equal(expected.EnabledEncodings, actual.EnabledEncodings);
        Assert.Equal(expected.SupportedProtocols, actual.SupportedProtocols);
        Assert.Equal(expected.MaxFramePayload, actual.MaxFramePayload);
        Assert.Equal(expected.ExtSupport, actual.ExtSupport);
        Assert.Equal(expected.MaxConcurrentStreams, actual.MaxConcurrentStreams);
    }

    private static NcpHandshakeAction ParseAction(string value) => value switch
    {
        "accept" => NcpHandshakeAction.Accept,
        "silent_close" => NcpHandshakeAction.SilentClose,
        "error_close" => NcpHandshakeAction.ErrorClose,
        _ => throw new InvalidOperationException($"Unknown vector action '{value}'."),
    };

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relative}'.");
    }

    private sealed record VectorSuite(
        string Name,
        string Version,
        IReadOnlyList<VectorCase> Vectors);

    private sealed record VectorCase(
        string Id,
        string Kind,
        VectorInput Input,
        VectorExpected Expected);

    private sealed record VectorInput(
        ServerInput Server,
        TransportInput Transport,
        HelloInput? Hello);

    private sealed record ServerInput
    {
        public string? MinVersion { get; init; }
        public string? NpsVersion { get; init; }
        public IReadOnlyList<string>? SupportedEncodings { get; init; }
        public IReadOnlyList<string>? SupportedProtocols { get; init; }
        public uint MaxFramePayload { get; init; }
        public bool ExtSupport { get; init; }
        public uint MaxConcurrentStreams { get; init; }
        public uint MaxHelloPayload { get; init; }
        public int PreambleTimeoutMs { get; init; }
        public int HelloTimeoutMs { get; init; }
    }

    private sealed record TransportInput
    {
        public required string PreambleHex { get; init; }
        public int PreambleElapsedMs { get; init; }
        public string? FirstFrameType { get; init; }
        public string? FirstFrameTier { get; init; }
        public bool FirstFrameEncrypted { get; init; }
        public bool FirstFrameExtended { get; init; }
        public uint HelloPayloadLength { get; init; }
        public int HelloElapsedMs { get; init; }
    }

    private sealed record HelloInput(
        string? MinVersion,
        string NpsVersion,
        IReadOnlyList<string> SupportedEncodings,
        IReadOnlyList<string> SupportedProtocols,
        uint MaxFramePayload,
        bool ExtSupport,
        uint MaxConcurrentStreams);

    private sealed record VectorExpected
    {
        public required string Action { get; init; }
        public string? Status { get; init; }
        public string? Error { get; init; }
        public string? DiagnosticError { get; init; }
        public string? SessionVersion { get; init; }
        public string? NegotiatedEncoding { get; init; }
        public IReadOnlyList<string>? EnabledEncodings { get; init; }
        public IReadOnlyList<string>? SupportedProtocols { get; init; }
        public uint? MaxFramePayload { get; init; }
        public bool? ExtSupport { get; init; }
        public uint? MaxConcurrentStreams { get; init; }
    }
}
