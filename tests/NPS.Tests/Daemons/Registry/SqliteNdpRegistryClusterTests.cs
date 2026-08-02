// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;
using NPS.Daemon.Registry;
using NPS.NDP;
using NPS.NDP.Frames;
using NPS.NDP.Registry;

namespace NPS.Tests.Daemons.Registry;

/// <summary>
/// NPS-CR-0009 multi-Anchor HA at the nps-registry storage layer: highest-<c>cluster_epoch</c>
/// resolution, the equal-epoch <c>NDP-CLUSTER-SPLIT</c> fault, epoch defaulting, the monotonic
/// per-cluster ownership tuple federated peers exchange (NDP §9), and the schema migration for
/// stores created before the CR.
/// </summary>
public sealed class SqliteNdpRegistryClusterTests
{
    private const string Cluster = "urn:nps:cluster:api.test:main";
    private const string AnchorA = "urn:nps:node:api.test:anchor-a";
    private const string AnchorB = "urn:nps:node:api.test:anchor-b";

    private static AnnounceFrame Member(
        string  nid,
        ulong?  epoch,
        string  cluster = Cluster,
        string  host    = "10.0.0.1",
        uint    ttl     = 300,
        string? timestamp = null) =>
        new()
        {
            Nid           = nid,
            NodeType      = "anchor",
            NodeRoles     = ["anchor"],
            ClusterAnchor = cluster,
            ClusterEpoch  = epoch,
            Addresses     = [new NdpAddress { Host = host, Port = 17433, Protocol = "nwp" }],
            Capabilities  = ["topology.read"],
            Ttl           = ttl,
            Timestamp     = timestamp ?? DateTime.UtcNow.ToString("O"),
            Signature     = "ed25519:placeholder", // local-dev registry does not verify
        };

    // ── Highest-epoch resolution (CR-0009 §3.4) ───────────────────────────────

    [Fact]
    public void ResolveCluster_ReturnsHighestEpochAnchor()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member(AnchorA, epoch: 1));
        reg.Announce(Member(AnchorB, epoch: 3));   // ownership failed over to B

        var active = reg.ResolveCluster(Cluster);

        Assert.NotNull(active);
        Assert.Equal(AnchorB, active!.Nid);
        Assert.Equal(3UL, active.ClusterEpoch);
    }

    [Fact]
    public void ResolveCluster_StaleLowerEpochAnnounce_DoesNotDisplaceActiveAnchor()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member(AnchorB, epoch: 3));
        reg.Announce(Member(AnchorA, epoch: 2));   // superseded leader keeps announcing

        var active = reg.ResolveCluster(Cluster);

        Assert.Equal(AnchorB, active!.Nid);
        Assert.Equal(3UL, active.ClusterEpoch);
        Assert.Equal(3UL, reg.GetClusterOwnership(Cluster)!.ClusterEpoch);
        Assert.Equal(AnchorB, reg.GetClusterOwnership(Cluster)!.ActiveNid);
    }

    [Fact]
    public void Announce_LowerEpochForSameAnchor_DoesNotDowngradeStoredFence()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member(AnchorB, epoch: 4));
        reg.Announce(Member(AnchorB, epoch: 2, host: "10.0.0.9")); // replayed / stale re-announce

        var stored = reg.GetByNid(AnchorB);
        Assert.Equal(4UL, stored!.ClusterEpoch);          // epoch is monotonic per Anchor
        Assert.Equal("10.0.0.9", stored.Addresses[0].Host); // the rest of the frame still refreshes
    }

    [Fact]
    public void ResolveCluster_AbsentEpochIsTreatedAsOne()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member("urn:nps:node:api.test:solo", epoch: null)); // single-Anchor, pre-CR peer

        var active = reg.ResolveCluster(Cluster);

        Assert.Equal("urn:nps:node:api.test:solo", active!.Nid);
        Assert.Equal(SqliteNdpRegistry.DefaultClusterEpoch, active.ClusterEpoch);
        Assert.Equal(1UL, reg.GetClusterOwnership(Cluster)!.ClusterEpoch);
    }

    [Fact]
    public void ResolveCluster_EqualTopEpoch_ThrowsClusterSplit()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member(AnchorA, epoch: 2));
        reg.Announce(Member(AnchorB, epoch: 2));   // two live actives at the same fence

        var ex = Assert.Throws<NdpClusterSplitException>(() => reg.ResolveCluster(Cluster));

        Assert.Equal(NdpErrorCodes.ClusterSplit, ex.ErrorCode);
        Assert.Equal(Cluster, ex.ClusterAnchor);
        Assert.Equal(2UL, ex.Epoch);
    }

    [Fact]
    public void ResolveCluster_NoLiveMembers_ReturnsNull()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        Assert.Null(reg.ResolveCluster(Cluster));
    }

    [Fact]
    public void ResolveCluster_IgnoresExpiredMembers()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        var oldTs = DateTime.UtcNow.AddSeconds(-2).ToString("O");
        reg.Announce(Member(AnchorA, epoch: 1));
        reg.Announce(Member(AnchorB, epoch: 5, ttl: 1, timestamp: oldTs)); // higher epoch, dead

        var active = reg.ResolveCluster(Cluster);
        Assert.Equal(AnchorA, active!.Nid);
    }

    [Fact]
    public void ResolveCluster_ScopesToTheRequestedCluster()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member(AnchorA, epoch: 1));
        reg.Announce(Member("urn:nps:node:other.test:anchor", epoch: 9,
                            cluster: "urn:nps:cluster:other.test:main"));

        Assert.Equal(AnchorA, reg.ResolveCluster(Cluster)!.Nid);
    }

    [Fact]
    public void ResolveCluster_ViaInterface_ResolvesHighestEpoch()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        INdpRegistry asInterface = reg;
        asInterface.Announce(Member(AnchorA, epoch: 1));
        asInterface.Announce(Member(AnchorB, epoch: 7));

        Assert.Equal(AnchorB, asInterface.ResolveCluster(Cluster)!.Nid);
    }

    // ── Federated ownership tuple (NDP §9) ────────────────────────────────────

    [Fact]
    public void ApplyClusterOwnership_HigherEpochFromPeer_IsPreferred()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member(AnchorA, epoch: 2));

        var outcome = reg.ApplyClusterOwnership(Cluster, 5, AnchorB, source: "urn:nps:agent:peer:r2");

        Assert.Equal(ClusterOwnershipOutcome.Applied, outcome);
        var owner = reg.GetClusterOwnership(Cluster);
        Assert.Equal(5UL, owner!.ClusterEpoch);
        Assert.Equal(AnchorB, owner.ActiveNid);
        Assert.Equal("urn:nps:agent:peer:r2", owner.Source);
    }

    [Fact]
    public void ApplyClusterOwnership_LowerEpochFromPeer_DoesNotDowngrade()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member(AnchorB, epoch: 4));

        var outcome = reg.ApplyClusterOwnership(Cluster, 2, AnchorA, source: "urn:nps:agent:peer:r2");

        Assert.Equal(ClusterOwnershipOutcome.Ignored, outcome);
        var owner = reg.GetClusterOwnership(Cluster);
        Assert.Equal(4UL, owner!.ClusterEpoch);
        Assert.Equal(AnchorB, owner.ActiveNid);
        Assert.Equal(SqliteNdpRegistry.LocalSource, owner.Source);
    }

    [Fact]
    public void ApplyClusterOwnership_SameEpochSameAnchor_IsIdempotent()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member(AnchorB, epoch: 4));

        Assert.Equal(ClusterOwnershipOutcome.Ignored,
            reg.ApplyClusterOwnership(Cluster, 4, AnchorB, source: "urn:nps:agent:peer:r2"));
    }

    [Fact]
    public void ApplyClusterOwnership_EqualEpochDifferentAnchor_ReportsSplit()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member(AnchorA, epoch: 3));

        var outcome = reg.ApplyClusterOwnership(Cluster, 3, AnchorB, source: "urn:nps:agent:peer:r2");

        Assert.Equal(ClusterOwnershipOutcome.Split, outcome);
        Assert.Equal(AnchorA, reg.GetClusterOwnership(Cluster)!.ActiveNid); // state untouched
    }

    [Fact]
    public void GetAllClusterOwnership_ListsEveryKnownCluster()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(Member(AnchorA, epoch: 1));
        reg.Announce(Member("urn:nps:node:other.test:anchor", epoch: 2,
                            cluster: "urn:nps:cluster:other.test:main"));

        var all = reg.GetAllClusterOwnership();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, o => o.ClusterAnchor == Cluster && o.ActiveNid == AnchorA);
    }

    [Fact]
    public void ClusterOwnership_UnknownCluster_IsNull()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        Assert.Null(reg.GetClusterOwnership(Cluster));
    }

    // ── Backward compatibility ────────────────────────────────────────────────

    [Fact]
    public void NonClusterAnnounce_RecordsNoOwnership()
    {
        using var reg = SqliteNdpRegistry.CreateInMemory();
        reg.Announce(new AnnounceFrame
        {
            Nid          = "urn:nps:node:api.test:products",
            NodeType     = "memory",
            Addresses    = [new NdpAddress { Host = "10.0.0.1", Port = 17434, Protocol = "nwp" }],
            Capabilities = ["nwp:query"],
            Ttl          = 300,
            Timestamp    = DateTime.UtcNow.ToString("O"),
            Signature    = "ed25519:placeholder",
        });

        Assert.Empty(reg.GetAllClusterOwnership());
        Assert.Null(reg.GetByNid("urn:nps:node:api.test:products")!.ClusterAnchor);
    }

    [Fact]
    public void MigratesPreCr0009Store_ExistingRowsDefaultToEpochOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlite-ndp-cr0009-{Guid.NewGuid():N}.db");
        try
        {
            // Recreate the alpha.16 schema — no cluster_anchor / cluster_epoch columns.
            using (var legacy = new SqliteConnection($"Data Source={path}"))
            {
                legacy.Open();
                using var cmd = legacy.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE announcements (
                      nid            TEXT PRIMARY KEY,
                      addresses_json TEXT NOT NULL,
                      caps_json      TEXT NOT NULL,
                      node_type      TEXT,
                      ttl            INTEGER NOT NULL,
                      timestamp      TEXT NOT NULL,
                      signature      TEXT NOT NULL,
                      expires_at     TEXT NOT NULL
                    );
                    CREATE TABLE graph_meta (
                      id  INTEGER PRIMARY KEY CHECK (id = 1),
                      seq INTEGER NOT NULL DEFAULT 0
                    );
                    INSERT INTO graph_meta (id, seq) VALUES (1, 7);
                    INSERT INTO announcements
                      (nid, addresses_json, caps_json, node_type, ttl, timestamp, signature, expires_at)
                    VALUES
                      ('urn:nps:node:api.test:legacy',
                       '[{"host":"10.0.0.1","port":17434,"protocol":"nwp"}]',
                       '["nwp:query"]', 'memory', 300, '2026-07-05T00:00:00Z', 'ed25519:placeholder',
                       '2099-01-01T00:00:00.0000000Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            using var reg = new SqliteNdpRegistry(path);

            var legacyRow = reg.GetByNid("urn:nps:node:api.test:legacy");
            Assert.NotNull(legacyRow);
            Assert.Null(legacyRow!.ClusterAnchor);
            Assert.Equal(SqliteNdpRegistry.DefaultClusterEpoch, legacyRow.ClusterEpoch);
            Assert.Equal(7UL, reg.GetSeq());

            // and the migrated store now accepts cluster announcements
            reg.Announce(Member(AnchorB, epoch: 6));
            Assert.Equal(6UL, reg.ResolveCluster(Cluster)!.ClusterEpoch);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ClusterState_PersistsAcrossReopens()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlite-ndp-cr0009-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new SqliteNdpRegistry(path))
            {
                first.Announce(Member(AnchorB, epoch: 3));
            }

            using var second = new SqliteNdpRegistry(path);
            Assert.Equal(AnchorB, second.ResolveCluster(Cluster)!.Nid);
            Assert.Equal(3UL, second.GetClusterOwnership(Cluster)!.ClusterEpoch);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
