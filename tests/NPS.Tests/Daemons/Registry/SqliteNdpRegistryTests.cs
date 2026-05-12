// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Daemon.Registry;
using NPS.NDP.Frames;

namespace NPS.Tests.Daemons.Registry;

/// <summary>
/// Behavioural parity tests for the SQLite-backed NDP registry used by the
/// nps-registry daemon (alpha.4 §Daemons). Mirrors the InMemoryNdpRegistry
/// suite where the contract is identical, plus SQLite-specific assertions
/// (lazy expiry, persistence across reopens, monotonic graph seq).
/// </summary>
public sealed class SqliteNdpRegistryTests
{
    private static AnnounceFrame MakeAnnounce(
        string nid,
        string host = "10.0.0.1",
        int    port = 17434,
        uint   ttl  = 300,
        string? timestamp = null) =>
        new()
        {
            Nid          = nid,
            NodeType     = "memory",
            Addresses    = [new NdpAddress { Host = host, Port = port, Protocol = "nwp" }],
            Capabilities = ["nwp:query"],
            Ttl          = ttl,
            Timestamp    = timestamp ?? DateTime.UtcNow.ToString("O"),
            Signature    = "ed25519:placeholder",
        };

    // ── Announce / GetByNid ───────────────────────────────────────────────────

    [Fact]
    public void Announce_StoresEntry_GetByNid_Returns()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products"));

        var result = reg.GetByNid("urn:nps:node:api.test:products");
        Assert.NotNull(result);
        Assert.Equal("urn:nps:node:api.test:products", result!.Nid);
        Assert.Equal("memory", result.NodeType);
    }

    [Fact]
    public void Announce_TtlZero_EvictsEntry()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:orders"));
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:orders", ttl: 0));

        Assert.Null(reg.GetByNid("urn:nps:node:api.test:orders"));
    }

    [Fact]
    public void Announce_RefreshesExistingEntry()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", host: "10.0.0.1"));
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", host: "10.0.0.2"));

        var result = reg.GetByNid("urn:nps:node:api.test:products");
        Assert.Equal("10.0.0.2", result!.Addresses[0].Host);
    }

    [Fact]
    public void GetByNid_ExpiredEntry_ReturnsNull()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        // ttl=1 + slept past expiry — uses the row's own timestamp, so we
        // backdate by setting timestamp to 2s ago.
        var oldTs = DateTime.UtcNow.AddSeconds(-2).ToString("O");
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:gone", ttl: 1, timestamp: oldTs));

        Assert.Null(reg.GetByNid("urn:nps:node:api.test:gone"));
    }

    // ── GetAll / lazy purge ───────────────────────────────────────────────────

    [Fact]
    public void GetAll_ReturnsLiveEntries_PurgesExpired()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        var oldTs = DateTime.UtcNow.AddSeconds(-2).ToString("O");

        reg.Announce(MakeAnnounce("urn:nps:node:api.test:live"));
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:expired", ttl: 1, timestamp: oldTs));

        var all = reg.GetAll();
        Assert.Single(all);
        Assert.Equal("urn:nps:node:api.test:live", all[0].Nid);
    }

    // ── Resolve ───────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_KnownTarget_ReturnsEndpoint()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", host: "10.0.0.5", port: 17434));

        var result = reg.Resolve("nwp://api.test/products");
        Assert.NotNull(result);
        Assert.Equal("10.0.0.5", result!.Host);
        Assert.Equal(17434, result.Port);
    }

    [Fact]
    public void Resolve_UnknownTarget_ReturnsNull()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products"));

        Assert.Null(reg.Resolve("nwp://api.test/inventory"));
    }

    [Fact]
    public void Resolve_AfterEviction_ReturnsNull()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:gone"));
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:gone", ttl: 0));

        Assert.Null(reg.Resolve("nwp://api.test/gone"));
    }

    // ── Graph seq counter ─────────────────────────────────────────────────────

    [Fact]
    public void GetSeq_StartsAtZero_IncrementsOnEachAnnounce()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        Assert.Equal(0UL, reg.GetSeq());

        reg.Announce(MakeAnnounce("urn:nps:node:a"));
        reg.Announce(MakeAnnounce("urn:nps:node:b"));
        reg.Announce(MakeAnnounce("urn:nps:node:b", ttl: 0));   // eviction also bumps

        Assert.Equal(3UL, reg.GetSeq());
    }

    // ── Persistence (file-backed) ─────────────────────────────────────────────

    [Fact]
    public void Persists_AcrossReopens_OnFileBackedStore()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"sqlite-ndp-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new SqliteNdpRegistry(path))
            {
                first.Announce(MakeAnnounce("urn:nps:node:persisted"));
            }

            using var second = new SqliteNdpRegistry(path);
            Assert.NotNull(second.GetByNid("urn:nps:node:persisted"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
