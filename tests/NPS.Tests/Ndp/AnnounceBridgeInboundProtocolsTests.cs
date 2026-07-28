// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NDP.Frames;
using NPS.NIP.Crypto;
using Xunit;

namespace NPS.Tests.Ndp;

/// <summary>
/// NPS-CR-0010 review finding F2: the reference SDK's AnnounceFrame must be able to carry
/// <c>bridge_inbound_protocols</c> — without it, a Bridge Node cannot announce that it serves a
/// direction, and the whole direction-declaration mechanism is dead on the .NET wire.
/// </summary>
public class AnnounceBridgeInboundProtocolsTests
{
    private static AnnounceFrame BidirectionalBridge() => new()
    {
        Nid = "urn:nps:node:example.com:bridge-1",
        Addresses = [new NdpAddress { Host = "10.0.0.5", Port = 17434, Protocol = "nwp" }],
        Capabilities = ["nwp:invoke"],
        Ttl = 300,
        Timestamp = "2026-07-16T00:00:00Z",
        NodeRoles = ["bridge"],
        BridgeProtocols = ["http", "grpc"],            // outbound
        BridgeInboundProtocols = ["mcp", "grpc"],      // inbound
        Signature = "ed25519:placeholder",
    };

    [Fact]
    public void BridgeInboundProtocols_RoundTripsOnTheWire()
    {
        var json = JsonSerializer.Serialize(BidirectionalBridge());
        Assert.Contains("bridge_inbound_protocols", json);

        var back = JsonSerializer.Deserialize<AnnounceFrame>(json)!;
        Assert.Equal(["mcp", "grpc"], back.BridgeInboundProtocols);
        Assert.Equal(["http", "grpc"], back.BridgeProtocols); // outbound set unchanged
    }

    [Fact]
    public void BridgeInboundProtocols_IsCoveredBySignature()
    {
        // The field is a security-relevant direction declaration, so it must be inside the signed
        // canonical form (not on the excluded liveness list). CanonicalJson excludes the advisory
        // fields (health/last_seen) but must retain both bridge_* arrays.
        var canonical = NipSigner.CanonicalJson(BidirectionalBridge());
        Assert.Contains("bridge_inbound_protocols", canonical);
        Assert.Contains("bridge_protocols", canonical);
    }

    [Fact]
    public void AbsentInboundProtocols_CostsExistingOutboundOnlyNodesNothing()
    {
        // An alpha.15 outbound-only Bridge Node leaves the field null; it must simply not appear.
        var outboundOnly = BidirectionalBridge() with { BridgeInboundProtocols = null };
        var json = JsonSerializer.Serialize(outboundOnly);
        Assert.DoesNotContain("bridge_inbound_protocols", json);
    }
}
