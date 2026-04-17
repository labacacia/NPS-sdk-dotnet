// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Frames;
using NPS.NDP.Frames;
using NPS.NDP.Registry;

namespace NPS.Tests.Ndp;

public sealed class NdpFrameTests
{
    // ── AnnounceFrame ─────────────────────────────────────────────────────────

    [Fact]
    public void AnnounceFrame_FrameType_IsAnnounce()
    {
        var frame = MakeAnnounce("urn:nps:node:api.test:products");
        Assert.Equal(FrameType.Announce, frame.FrameType);
        Assert.Equal(EncodingTier.MsgPack, frame.PreferredTier);
        Assert.Equal("0x30", frame.Frame);
    }

    [Fact]
    public void AnnounceFrame_Shutdown_TtlZero()
    {
        var frame = MakeAnnounce("urn:nps:node:api.test:products", ttl: 0);
        Assert.Equal(0u, frame.Ttl);
    }

    [Fact]
    public void AnnounceFrame_RoundTrip_Json()
    {
        var frame = MakeAnnounce("urn:nps:node:api.test:products");
        var json  = JsonSerializer.Serialize(frame);
        var back  = JsonSerializer.Deserialize<AnnounceFrame>(json)!;

        Assert.Equal(frame.Nid, back.Nid);
        Assert.Equal(frame.Ttl, back.Ttl);
        Assert.Equal(frame.Addresses.Count, back.Addresses.Count);
        Assert.Equal(frame.Addresses[0].Host, back.Addresses[0].Host);
        Assert.Equal(frame.Capabilities, back.Capabilities);
    }

    // ── ResolveFrame ──────────────────────────────────────────────────────────

    [Fact]
    public void ResolveFrame_Request_FrameType()
    {
        var req = new ResolveFrame { Target = "nwp://api.test/products" };
        Assert.Equal(FrameType.Resolve, req.FrameType);
        Assert.Equal(EncodingTier.Json, req.PreferredTier);
        Assert.Equal("0x31", req.Frame);
        Assert.Null(req.Resolved);
    }

    [Fact]
    public void ResolveFrame_Response_HasResolved()
    {
        var resp = new ResolveFrame
        {
            Target   = "nwp://api.test/products",
            Resolved = new NdpResolveResult
            {
                Host = "10.0.0.5",
                Port = 17434,
                Ttl  = 300,
            },
        };
        Assert.NotNull(resp.Resolved);
        Assert.Equal("10.0.0.5", resp.Resolved.Host);
    }

    [Fact]
    public void ResolveFrame_RoundTrip_Json()
    {
        var frame = new ResolveFrame
        {
            Target       = "nwp://api.test/orders",
            RequesterNid = "urn:nps:agent:ca.test:agent-1",
            Resolved     = new NdpResolveResult
            {
                Host             = "192.168.1.10",
                Port             = 17434,
                CertFingerprint  = "sha256:abcdef1234",
                Ttl              = 60,
            },
        };
        var json = JsonSerializer.Serialize(frame);
        var back = JsonSerializer.Deserialize<ResolveFrame>(json)!;

        Assert.Equal(frame.Target, back.Target);
        Assert.Equal(frame.RequesterNid, back.RequesterNid);
        Assert.Equal(frame.Resolved!.Host, back.Resolved!.Host);
        Assert.Equal(frame.Resolved.CertFingerprint, back.Resolved.CertFingerprint);
    }

    // ── GraphFrame ────────────────────────────────────────────────────────────

    [Fact]
    public void GraphFrame_InitialSync_FrameType()
    {
        var frame = new GraphFrame
        {
            InitialSync = true,
            Nodes       = [new NdpGraphNode
            {
                Nid          = "urn:nps:node:api.test:products",
                NodeType     = "memory",
                Addresses    = [new NdpAddress { Host = "10.0.0.1", Port = 17434, Protocol = "nwp" }],
                Capabilities = ["nwp:query"],
            }],
            Seq = 1,
        };
        Assert.Equal(FrameType.Graph, frame.FrameType);
        Assert.Equal(EncodingTier.MsgPack, frame.PreferredTier);
        Assert.Equal("0x32", frame.Frame);
        Assert.NotNull(frame.Nodes);
        Assert.Single(frame.Nodes);
    }

    [Fact]
    public void GraphFrame_Incremental_HasPatch()
    {
        var patch = JsonDocument.Parse("""[{"op":"replace","path":"/nodes/0/ttl","value":600}]""");
        var frame = new GraphFrame
        {
            InitialSync = false,
            Patch       = patch.RootElement,
            Seq         = 2,
        };
        Assert.False(frame.InitialSync);
        Assert.Null(frame.Nodes);
        Assert.NotNull(frame.Patch);
        Assert.Equal(JsonValueKind.Array, frame.Patch.Value.ValueKind);
    }

    // ── NwpTargetMatchesNid ───────────────────────────────────────────────────

    [Theory]
    [InlineData("urn:nps:node:api.example.com:products", "nwp://api.example.com/products", true)]
    [InlineData("urn:nps:node:api.example.com:products", "nwp://api.example.com/products/123", true)]
    [InlineData("urn:nps:node:api.example.com:products", "nwp://api.example.com/orders", false)]
    [InlineData("urn:nps:node:api.example.com:products", "nwp://other.example.com/products", false)]
    [InlineData("urn:nps:node:api.example.com:products", "nwp://API.EXAMPLE.COM/products", true)]
    [InlineData("urn:nps:agent:ca.example.com:a1", "nwp://ca.example.com/a1/data", true)]
    [InlineData("urn:nps:node:api.example.com:products", "http://api.example.com/products", false)]
    public void NwpTargetMatchesNid_ReturnsExpected(string nid, string target, bool expected)
    {
        Assert.Equal(expected, InMemoryNdpRegistry.NwpTargetMatchesNid(nid, target));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static AnnounceFrame MakeAnnounce(string nid, uint ttl = 300) =>
        new()
        {
            Nid          = nid,
            NodeType     = "memory",
            Addresses    = [new NdpAddress { Host = "10.0.0.1", Port = 17434, Protocol = "nwp" }],
            Capabilities = ["nwp:query", "nwp:stream"],
            Ttl          = ttl,
            Timestamp    = DateTime.UtcNow.ToString("O"),
            Signature    = "ed25519:placeholder",
        };
}
