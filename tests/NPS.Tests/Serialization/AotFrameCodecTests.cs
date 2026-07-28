// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Registry;
using NPS.NDP.Frames;
using NPS.NDP.Registry;
using NPS.NIP;
using NPS.NIP.Frames;
using NPS.NIP.Registry;

namespace NPS.Tests.Serialization;

public sealed class AotFrameCodecTests
{
    [Fact]
    public void NipGeneratedMetadata_RoundTripsIdentityFrame()
    {
        var registry = new FrameRegistryBuilder()
            .AddNcp()
            .AddNip()
            .Build();
        var codec = NpsFrameCodec.Create(registry);
        var frame = new IdentFrame
        {
            Nid = "urn:nps:agent:example.test:alice",
            PubKey = "ed25519:test",
            Capabilities = ["nwp:query", "nwp:invoke"],
            Scope = JsonDocument.Parse("""{"nodes":["nwp://example.test/*"]}""").RootElement,
            IssuedBy = "urn:nps:org:example.test",
            IssuedAt = "2026-07-28T00:00:00Z",
            ExpiresAt = "2026-07-29T00:00:00Z",
            Serial = "01",
            Signature = "ed25519:test",
            AssuranceLevel = AssuranceLevel.Attested,
        };

        var result = Assert.IsType<IdentFrame>(
            codec.Decode(codec.Encode(frame, EncodingTier.MsgPack)));

        Assert.Equal(frame.Nid, result.Nid);
        Assert.Equal(AssuranceLevel.Attested, result.AssuranceLevel);
        Assert.Equal(
            "nwp://example.test/*",
            result.Scope.GetProperty("nodes")[0].GetString());
    }

    [Fact]
    public void NdpGeneratedMetadata_RoundTripsGraphFrame()
    {
        var registry = new FrameRegistryBuilder()
            .AddNcp()
            .AddNdp()
            .Build();
        var codec = NpsFrameCodec.Create(registry);
        var frame = new GraphFrame
        {
            GraphId = "graph-1",
            Nodes =
            [
                new NdpGraphNode
                {
                    Nid = "urn:nps:node:example.test:a",
                    NodeRoles = ["memory"],
                },
                new NdpGraphNode
                {
                    Nid = "urn:nps:node:example.test:b",
                    NodeRoles = ["action"],
                },
            ],
            Edges =
            [
                new NdpGraphEdge
                {
                    FromNid = "urn:nps:node:example.test:a",
                    ToNid = "urn:nps:node:example.test:b",
                    LatencyMs = 12,
                    Protocol = "ncp",
                },
            ],
            Ttl = 60,
            Metadata = JsonDocument.Parse("""{"region":"ap-southeast-2"}""").RootElement,
        };

        var result = Assert.IsType<GraphFrame>(
            codec.Decode(codec.Encode(frame, EncodingTier.MsgPack)));

        Assert.Equal(frame.GraphId, result.GraphId);
        Assert.Equal(2, result.Nodes.Count);
        Assert.Single(result.Edges);
        Assert.Equal(
            "ap-southeast-2",
            result.Metadata!.Value.GetProperty("region").GetString());
    }
}
