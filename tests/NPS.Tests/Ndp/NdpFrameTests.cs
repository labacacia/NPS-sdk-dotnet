// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Linq;
using NPS.Core.Frames;
using NPS.NDP;
using NPS.NDP.Frames;
using NPS.NDP.Registry;
using NPS.NIP.Crypto;

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
        Assert.Equal(60_000u, back.HeartbeatIntervalMs);
        Assert.Equal(frame.Addresses.Count, back.Addresses.Count);
        Assert.Equal(frame.Addresses[0].Host, back.Addresses[0].Host);
        Assert.Equal(frame.Capabilities, back.Capabilities);
    }

    [Fact]
    public void AnnounceFrame_CanonicalJson_UsesNdpSignedBody()
    {
        var frame = MakeAnnounce("urn:nps:node:api.test:products") with
        {
            NodeType = null,
            Health = "draining",
            LastSeen = "2026-06-13T00:00:00Z",
        };

        var canonical = NipSigner.CanonicalJson(frame);

        Assert.Contains("\"heartbeat_interval_ms\":60000", canonical);
        Assert.DoesNotContain("\"frame\"", canonical);
        Assert.DoesNotContain("\"signature\"", canonical);
        Assert.DoesNotContain("\"health\"", canonical);
        Assert.DoesNotContain("\"last_seen\"", canonical);
        Assert.DoesNotContain("\"node_type\"", canonical);
    }

    [Fact]
    public void AnnounceFrame_ActivationEndpoint_RoundTripsAsAddressObject()
    {
        var frame = MakeAnnounce("urn:nps:node:api.test:products") with
        {
            ActivationMode = "resident",
            ActivationEndpoint = new NdpAddress { Host = "10.0.0.5", Port = 17440, Protocol = "nwp" },
        };
        var json = JsonSerializer.Serialize(frame);
        using var doc = JsonDocument.Parse(json);
        var endpoint = doc.RootElement.GetProperty("activation_endpoint");

        Assert.Equal("10.0.0.5", endpoint.GetProperty("host").GetString());
        Assert.Equal(17440, endpoint.GetProperty("port").GetInt32());

        var back = JsonSerializer.Deserialize<AnnounceFrame>(json)!;
        Assert.Equal("resident", back.ActivationMode);
        Assert.NotNull(back.ActivationEndpoint);
        Assert.Equal("nwp", back.ActivationEndpoint!.Protocol);
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
    public void GraphFrame_TopologySnapshot_FrameType()
    {
        var frame = new GraphFrame
        {
            GraphId = "snap-001",
            Nodes   = [new NdpGraphNode
            {
                Nid       = "urn:nps:node:api.test:products",
                NodeRoles = ["memory"],
            }, new NdpGraphNode
            {
                Nid       = "urn:nps:node:api.test:router",
                NodeRoles = ["router"],
            }],
            Edges = [new NdpGraphEdge
            {
                FromNid  = "urn:nps:node:api.test:router",
                ToNid    = "urn:nps:node:api.test:products",
                Protocol = "ncp",
            }],
            Ttl = 300,
        };
        Assert.Equal(FrameType.Graph, frame.FrameType);
        Assert.Equal(EncodingTier.MsgPack, frame.PreferredTier);
        Assert.Equal("0x32", frame.Frame);
        Assert.NotNull(frame.Nodes);
        Assert.Equal(2, frame.Nodes.Count);
        Assert.Single(frame.Edges);
        frame.Validate();
    }

    [Fact]
    public void GraphFrame_Validate_RejectsTooLarge()
    {
        var frame = new GraphFrame
        {
            GraphId = "too-big",
            Nodes = Enumerable.Range(0, 257)
                .Select(i => new NdpGraphNode { Nid = $"urn:nps:node:example.com:{i}" })
                .ToArray(),
            Edges = [],
            Ttl = 60,
        };

        var ex = Assert.Throws<ArgumentException>(frame.Validate);
        Assert.Contains(NdpErrorCodes.GraphTooLarge, ex.Message);
    }

    [Fact]
    public void GraphFrame_Validate_RejectsInvalidEdges()
    {
        var nodes = new[] { new NdpGraphNode { Nid = "urn:nps:node:example.com:a" } };
        var selfEdge = new GraphFrame
        {
            GraphId = "self-edge",
            Nodes = nodes,
            Edges = [new NdpGraphEdge { FromNid = nodes[0].Nid, ToNid = nodes[0].Nid }],
            Ttl = 60,
        };
        var ex1 = Assert.Throws<ArgumentException>(selfEdge.Validate);
        Assert.Contains(NdpErrorCodes.GraphInvalid, ex1.Message);

        var missingEndpoint = new GraphFrame
        {
            GraphId = "missing-edge",
            Nodes = nodes,
            Edges = [new NdpGraphEdge { FromNid = nodes[0].Nid, ToNid = "urn:nps:node:example.com:missing" }],
            Ttl = 60,
        };
        var ex2 = Assert.Throws<ArgumentException>(missingEndpoint.Validate);
        Assert.Contains(NdpErrorCodes.GraphInvalid, ex2.Message);
    }

    [Fact]
    public void Federation_ForwardedByHelpers()
    {
        const string header =
            "urn:nps:agent:registry-a.example.com:r1, urn:nps:agent:registry-b.example.com:r2";

        Assert.Equal(
            ["urn:nps:agent:registry-a.example.com:r1", "urn:nps:agent:registry-b.example.com:r2"],
            NdpFederation.ParseForwardedBy(header));

        var next = NdpFederation.AppendForwardedBy("urn:nps:agent:registry-c.example.com:r3", header);
        Assert.NotNull(next);
        Assert.Contains("registry-c", next);

        var loop = Assert.Throws<ArgumentException>(() =>
            NdpFederation.AppendForwardedBy("urn:nps:agent:registry-b.example.com:r2", header));
        Assert.Contains(NdpErrorCodes.FederationLoop, loop.Message);

        var dropped = NdpFederation.AppendForwardedBy(
            "urn:nps:agent:registry-d.example.com:r4",
            header + ", urn:nps:agent:registry-c.example.com:r3");
        Assert.Null(dropped);
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
