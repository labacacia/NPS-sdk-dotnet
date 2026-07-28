using NPS.NWP.Anchor.Topology;
using Xunit;

namespace NPS.Tests.Nwp;

/// <summary>NPS-CR-0009: anchor_failover / anchor_quorum_lost topology sub-type factories.</summary>
public sealed class NwpAnchorFailoverTests
{
    [Fact]
    public void Failover_event_carries_successor_epoch_reason()
    {
        var e = AnchorState.Failover("urn:nps:node:x:anchor-b", clusterEpoch: 3, reason: "active_lost");
        Assert.Equal(AnchorState.FieldAnchorFailover, e.Field);
        var d = e.Details!.Value;
        Assert.Equal("urn:nps:node:x:anchor-b", d.GetProperty("successor_nid").GetString());
        Assert.Equal(3ul, d.GetProperty("cluster_epoch").GetUInt64());
        Assert.Equal("active_lost", d.GetProperty("reason").GetString());
    }

    [Fact]
    public void QuorumLost_event_carries_counts()
    {
        var e = AnchorState.QuorumLost(quorumSize: 3, available: 1);
        Assert.Equal(AnchorState.FieldAnchorQuorumLost, e.Field);
        var d = e.Details!.Value;
        Assert.Equal(3u, d.GetProperty("quorum_size").GetUInt32());
        Assert.Equal(1u, d.GetProperty("available").GetUInt32());
    }

    [Fact]
    public void Failover_reason_defaults_to_planned()
    {
        var e = AnchorState.Failover("urn:nps:node:x:anchor-b", clusterEpoch: 2);
        Assert.Equal("planned", e.Details!.Value.GetProperty("reason").GetString());
    }
}
