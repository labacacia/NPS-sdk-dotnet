// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Registry;

namespace NPS.Tests.Ncp;

/// <summary>
/// Encode / decode round-trip tests for <see cref="HelloFrame"/> (NPS-1 §4.6).
/// </summary>
public sealed class HelloFrameTests
{
    private static readonly FrameRegistry    Registry = FrameRegistry.CreateDefault();
    private static readonly Tier1JsonCodec   Json     = new();
    private static readonly Tier2MsgPackCodec MsgPack = new();

    private static NpsFrameCodec Codec() => new(Json, MsgPack, Registry);

    private static HelloFrame MakeHello(string? agentId = null) => new()
    {
        NpsVersion          = "0.4",
        MinVersion          = "0.3",
        SupportedEncodings  = ["msgpack", "json"],
        SupportedProtocols  = ["ncp", "nwp", "nip"],
        AgentId             = agentId,
        MaxFramePayload     = 65_535,
        ExtSupport          = false,
        MaxConcurrentStreams = 16,
        E2EEncAlgorithms    = ["aes-256-gcm", "chacha20-poly1305"],
    };

    // ── RoundTrip ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void HelloFrame_RoundTrip(EncodingTier tier)
    {
        var frame  = MakeHello("urn:nps:agent:ca.innolotus.com:550e8400");
        var codec  = Codec();
        var wire   = codec.Encode(frame, tier);
        var result = (HelloFrame)codec.Decode(wire);

        Assert.Equal(frame.NpsVersion,           result.NpsVersion);
        Assert.Equal(frame.MinVersion,            result.MinVersion);
        Assert.Equal(frame.SupportedEncodings,    result.SupportedEncodings);
        Assert.Equal(frame.SupportedProtocols,    result.SupportedProtocols);
        Assert.Equal(frame.AgentId,               result.AgentId);
        Assert.Equal(frame.MaxFramePayload,       result.MaxFramePayload);
        Assert.Equal(frame.ExtSupport,            result.ExtSupport);
        Assert.Equal(frame.MaxConcurrentStreams,  result.MaxConcurrentStreams);
        Assert.Equal(frame.E2EEncAlgorithms,      result.E2EEncAlgorithms);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void HelloFrame_NullAgentId_RoundTrip(EncodingTier tier)
    {
        var frame  = MakeHello();    // AgentId = null
        var wire   = Codec().Encode(frame, tier);
        var result = (HelloFrame)Codec().Decode(wire);

        Assert.Null(result.AgentId);
        Assert.Equal("0.4", result.NpsVersion);
    }

    // ── Wire header ──────────────────────────────────────────────────────────

    [Fact]
    public void HelloFrame_WireHeader_HasCorrectFrameType()
    {
        var wire = Codec().Encode(MakeHello(), EncodingTier.Json);
        Assert.Equal((byte)FrameType.Hello, wire[0]);
    }

    [Fact]
    public void HelloFrame_PreferredTier_IsJson()
    {
        // Spec: HelloFrame uses Tier-1 JSON during handshake (encoding not yet negotiated).
        Assert.Equal(EncodingTier.Json, new HelloFrame
        {
            NpsVersion         = "0.4",
            SupportedEncodings = ["json"],
            SupportedProtocols = ["ncp"],
        }.PreferredTier);
    }

    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void HelloFrame_Defaults_AreCorrect()
    {
        var frame = new HelloFrame
        {
            NpsVersion         = "0.4",
            SupportedEncodings = ["json"],
            SupportedProtocols = ["ncp"],
        };

        Assert.Equal((uint)FrameHeader.DefaultMaxPayload, frame.MaxFramePayload);
        Assert.False(frame.ExtSupport);
        Assert.Equal(32u, frame.MaxConcurrentStreams);
        Assert.Null(frame.E2EEncAlgorithms);
        Assert.Null(frame.MinVersion);
        Assert.Null(frame.AgentId);
    }

    // ── Registry ─────────────────────────────────────────────────────────────

    [Fact]
    public void FrameRegistry_Default_ContainsHello()
    {
        var type = Registry.Resolve(FrameType.Hello);
        Assert.Equal(typeof(HelloFrame), type);
    }
}
