using NPS.NDP;
using NPS.NDP.Frames;
using NPS.NDP.Registry;
using Xunit;

namespace NPS.Tests.Ndp;

/// <summary>NPS-CR-0009 multi-Anchor HA: cluster-anchor resolution by highest cluster_epoch.</summary>
public sealed class NdpClusterResolutionTests
{
    private const string Cluster = "urn:nps:cluster:api.test:main";

    private static AnnounceFrame Member(string nid, ulong? epoch) => new()
    {
        Nid           = nid,
        NodeType      = "anchor",
        ClusterAnchor = Cluster,
        ClusterEpoch  = epoch,
        NodeRoles     = ["anchor"],
        Addresses     = [new NdpAddress { Host = "10.0.0.1", Port = 17433, Protocol = "nwp" }],
        Capabilities  = ["topology.read"],
        Ttl           = 3600,
        Timestamp     = "2026-07-05T00:00:00Z",
        Signature     = "ed25519:placeholder", // local-dev registry does not verify
    };

    [Fact]
    public void Resolves_the_highest_epoch_active_anchor()
    {
        INdpRegistry reg = new InMemoryNdpRegistry();
        reg.Announce(Member("urn:nps:node:api.test:anchor-a", epoch: 1));
        reg.Announce(Member("urn:nps:node:api.test:anchor-b", epoch: 3)); // failed over to b

        var active = reg.ResolveCluster(Cluster);

        Assert.NotNull(active);
        Assert.Equal("urn:nps:node:api.test:anchor-b", active!.Nid);
        Assert.Equal(3ul, active.ClusterEpoch);
    }

    [Fact]
    public void Absent_epoch_is_treated_as_one()
    {
        INdpRegistry reg = new InMemoryNdpRegistry();
        reg.Announce(Member("urn:nps:node:api.test:solo", epoch: null)); // single-Anchor, no epoch
        var active = reg.ResolveCluster(Cluster);
        Assert.Equal("urn:nps:node:api.test:solo", active!.Nid);
    }

    [Fact]
    public void Split_brain_at_the_top_epoch_throws()
    {
        INdpRegistry reg = new InMemoryNdpRegistry();
        reg.Announce(Member("urn:nps:node:api.test:anchor-a", epoch: 2));
        reg.Announce(Member("urn:nps:node:api.test:anchor-b", epoch: 2)); // two active at epoch 2

        var ex = Assert.Throws<NdpClusterSplitException>(() => reg.ResolveCluster(Cluster));
        Assert.Equal(NdpErrorCodes.ClusterSplit, ex.ErrorCode);
        Assert.Equal(2ul, ex.Epoch);
    }

    [Fact]
    public void No_live_members_resolves_to_null()
    {
        INdpRegistry reg = new InMemoryNdpRegistry();
        Assert.Null(reg.ResolveCluster(Cluster));
    }
}
