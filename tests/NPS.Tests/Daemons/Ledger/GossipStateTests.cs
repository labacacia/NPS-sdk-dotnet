// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Daemon.Ledger;
using NPS.NIP.Crypto;
using NSec.Cryptography;

namespace NPS.Tests.Daemons.Ledger;

/// <summary>
/// Behavioural tests for <see cref="GossipState"/>: peer caching, monotonicity
/// enforcement, and STH acceptance semantics (RFC-0004 §4.5).
/// </summary>
public sealed class GossipStateTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static SignedTreeHead MakeSth(string logId, ulong treeSize) => new()
    {
        LogId          = logId,
        TreeSize       = treeSize,
        Timestamp      = DateTimeOffset.UtcNow.ToString("O"),
        Sha256RootHash = "aabbcc",
        Signature      = "ed25519:placeholder",
    };

    // ── interval clamping ────────────────────────────────────────────────────

    [Fact]
    public void IntervalClamped_BelowMinimum_BecomesTen()
    {
        var state = new GossipState([], 2);
        Assert.Equal(10, state.IntervalSeconds);
    }

    [Fact]
    public void IntervalClamped_AboveMaximum_Becomes3600()
    {
        var state = new GossipState([], 99999);
        Assert.Equal(3600, state.IntervalSeconds);
    }

    [Fact]
    public void IntervalWithinRange_IsPreservedAsIs()
    {
        var state = new GossipState([], 60);
        Assert.Equal(60, state.IntervalSeconds);
    }

    // ── empty state ──────────────────────────────────────────────────────────

    [Fact]
    public void CurrentPeerSths_EmptyWhenNothingAccepted()
    {
        var state = new GossipState([], 30);
        Assert.Empty(state.CurrentPeerSths());
    }

    [Fact]
    public void LastAcceptedTreeSize_UnknownPeer_ReturnsZero()
    {
        var state = new GossipState([], 30);
        Assert.Equal(0UL, state.LastAcceptedTreeSize("urn:nps:log:operator-unknown"));
    }

    // ── AcceptPeerSth ────────────────────────────────────────────────────────

    [Fact]
    public void AcceptPeerSth_StoredAndRetrievable()
    {
        var state = new GossipState([], 30);
        var sth   = MakeSth("urn:nps:log:operator-A", 5);

        state.AcceptPeerSth("urn:nps:log:operator-A", sth);

        Assert.Equal(5UL, state.LastAcceptedTreeSize("urn:nps:log:operator-A"));
        var cached = state.CurrentPeerSths();
        Assert.Single(cached);
        Assert.Equal("urn:nps:log:operator-A", cached[0].LogId);
        Assert.Equal(5UL, cached[0].Sth.TreeSize);
    }

    [Fact]
    public void AcceptPeerSth_LaterAcceptance_OverwritesPrevious()
    {
        var state = new GossipState([], 30);
        state.AcceptPeerSth("urn:nps:log:operator-A", MakeSth("urn:nps:log:operator-A", 3));
        state.AcceptPeerSth("urn:nps:log:operator-A", MakeSth("urn:nps:log:operator-A", 7));

        Assert.Equal(7UL, state.LastAcceptedTreeSize("urn:nps:log:operator-A"));
        Assert.Single(state.CurrentPeerSths());
        Assert.Equal(7UL, state.CurrentPeerSths()[0].Sth.TreeSize);
    }

    [Fact]
    public void CurrentPeerSths_IncludesAllDistinctPeers()
    {
        var state = new GossipState([], 30);
        state.AcceptPeerSth("urn:nps:log:operator-A", MakeSth("urn:nps:log:operator-A", 1));
        state.AcceptPeerSth("urn:nps:log:operator-B", MakeSth("urn:nps:log:operator-B", 2));
        state.AcceptPeerSth("urn:nps:log:operator-C", MakeSth("urn:nps:log:operator-C", 3));

        var all = state.CurrentPeerSths();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, r => r.LogId == "urn:nps:log:operator-A");
        Assert.Contains(all, r => r.LogId == "urn:nps:log:operator-B");
        Assert.Contains(all, r => r.LogId == "urn:nps:log:operator-C");
    }

    // ── ReceivedAt timestamp ─────────────────────────────────────────────────

    [Fact]
    public void AcceptPeerSth_RecordCarriesReceivedAtTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var state  = new GossipState([], 30);
        state.AcceptPeerSth("urn:nps:log:operator-A", MakeSth("urn:nps:log:operator-A", 1));
        var after  = DateTimeOffset.UtcNow;

        var record = state.CurrentPeerSths()[0];
        var receivedAt = DateTimeOffset.Parse(record.ReceivedAt,
            null, System.Globalization.DateTimeStyles.RoundtripKind);

        Assert.True(receivedAt >= before && receivedAt <= after);
    }

    // ── FromEnvironment ──────────────────────────────────────────────────────

    [Fact]
    public void FromEnvironment_NoPeersEnvVar_ReturnsEmptyPeerList()
    {
        // Ensure env var is absent for this test.
        var prev = Environment.GetEnvironmentVariable("NPSLEDGER_PEERS");
        Environment.SetEnvironmentVariable("NPSLEDGER_PEERS", null);
        try
        {
            var state = GossipState.FromEnvironment();
            Assert.Empty(state.Peers);
            Assert.Equal(30, state.IntervalSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NPSLEDGER_PEERS", prev);
        }
    }

    [Fact]
    public void FromEnvironment_ParsesPeersJson()
    {
        const string json = """
            [{"log_id":"urn:nps:log:operator-X","endpoint":"http://localhost:17440"}]
            """;
        var prevPeers    = Environment.GetEnvironmentVariable("NPSLEDGER_PEERS");
        var prevInterval = Environment.GetEnvironmentVariable("NPSLEDGER_GOSSIP_INTERVAL_S");
        Environment.SetEnvironmentVariable("NPSLEDGER_PEERS", json);
        Environment.SetEnvironmentVariable("NPSLEDGER_GOSSIP_INTERVAL_S", "60");
        try
        {
            var state = GossipState.FromEnvironment();
            Assert.Single(state.Peers);
            Assert.Equal("urn:nps:log:operator-X", state.Peers[0].LogId);
            Assert.Equal("http://localhost:17440",  state.Peers[0].Endpoint);
            Assert.Null(state.Peers[0].PubKey);
            Assert.Equal(60, state.IntervalSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NPSLEDGER_PEERS",             prevPeers);
            Environment.SetEnvironmentVariable("NPSLEDGER_GOSSIP_INTERVAL_S", prevInterval);
        }
    }

    [Fact]
    public void FromEnvironment_ParsesPeerWithPubKey()
    {
        const string json = """
            [{"log_id":"urn:nps:log:operator-Y","endpoint":"http://peer:17440","pub_key":"ed25519:abc123"}]
            """;
        var prev = Environment.GetEnvironmentVariable("NPSLEDGER_PEERS");
        Environment.SetEnvironmentVariable("NPSLEDGER_PEERS", json);
        try
        {
            var state = GossipState.FromEnvironment();
            Assert.Single(state.Peers);
            Assert.Equal("ed25519:abc123", state.Peers[0].PubKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NPSLEDGER_PEERS", prev);
        }
    }

    [Fact]
    public void FromEnvironment_MalformedJson_FallsBackToEmptyList()
    {
        var prev = Environment.GetEnvironmentVariable("NPSLEDGER_PEERS");
        Environment.SetEnvironmentVariable("NPSLEDGER_PEERS", "not-json{{{{");
        try
        {
            var state = GossipState.FromEnvironment();
            Assert.Empty(state.Peers);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NPSLEDGER_PEERS", prev);
        }
    }

    // ── signature round-trip (integration with NipSigner) ────────────────────

    [Fact]
    public void AcceptedSth_SignatureRemainsVerifiable_AfterCaching()
    {
        // Simulate what GossipService does after validating a peer's STH:
        // the cached record's STH signature must still verify against the
        // original signing key.
        using var key = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var unsigned = new SignedTreeHead
        {
            LogId          = "urn:nps:log:operator-remote",
            TreeSize       = 42,
            Timestamp      = "2026-05-01T00:00:00Z",
            Sha256RootHash = "deadbeef",
            Signature      = string.Empty,
        };
        var sig    = NipSigner.Sign(key, unsigned);
        var signed = unsigned with { Signature = sig };

        var state = new GossipState([], 30);
        state.AcceptPeerSth("urn:nps:log:operator-remote", signed);

        var cached = state.CurrentPeerSths()[0].Sth;
        Assert.True(NipSigner.Verify(
            key.PublicKey,
            cached with { Signature = string.Empty },
            cached.Signature));
    }
}
