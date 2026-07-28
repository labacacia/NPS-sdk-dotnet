using NPS.NOP.Frames;
using NPS.NOP.Orchestration;
using Xunit;

namespace NPS.Tests.Nop;

/// <summary>NOP v0.8 (NPS-CR-0009): delegate-target resolution against the active cluster Anchor.</summary>
public sealed class ClusterDelegationResolverTests
{
    private const string Cluster = "urn:nps:cluster:api.test:main";

    private static DelegateFrame Delegate(string? cluster = null, string worker = "urn:nps:agent:x:w1") => new()
    {
        ParentTaskId        = "t1",
        SubtaskId           = "s1",
        NodeId              = "n1",
        TargetAgentNid      = worker,
        TargetClusterAnchor = cluster,
        Action              = "do",
        DelegatedScope      = System.Text.Json.JsonDocument.Parse("{}").RootElement,
        DeadlineAt          = "2026-08-01T00:00:00Z",
    };

    [Fact]
    public void Without_cluster_target_uses_agent_nid()
    {
        var r = new ClusterDelegationResolver(_ => throw new InvalidOperationException("must not resolve"));
        Assert.Equal("urn:nps:agent:x:w1", r.ResolveDelegateTarget(Delegate()));
    }

    [Fact]
    public void Cluster_target_resolves_to_active_anchor_and_caches()
    {
        var calls = 0;
        var r = new ClusterDelegationResolver(_ => { calls++; return new ClusterAnchorInfo("urn:nps:node:x:anchor-a", 1); });

        Assert.Equal("urn:nps:node:x:anchor-a", r.ResolveDelegateTarget(Delegate(Cluster)));
        Assert.Equal("urn:nps:node:x:anchor-a", r.ResolveDelegateTarget(Delegate(Cluster)));
        Assert.Equal(1, calls); // cached after first NDP lookup
    }

    [Fact]
    public void Failover_event_redirects_subsequent_delegations_to_successor()
    {
        var r = new ClusterDelegationResolver(_ => new ClusterAnchorInfo("urn:nps:node:x:anchor-a", 1));
        r.ResolveDelegateTarget(Delegate(Cluster)); // warm cache at epoch 1

        Assert.True(r.OnAnchorFailover(Cluster, "urn:nps:node:x:anchor-b", clusterEpoch: 2));
        Assert.Equal("urn:nps:node:x:anchor-b", r.ResolveDelegateTarget(Delegate(Cluster)));
    }

    [Fact]
    public void Stale_failover_event_is_ignored()
    {
        var r = new ClusterDelegationResolver(_ => new ClusterAnchorInfo("urn:nps:node:x:anchor-b", 3));
        r.ResolveActive(Cluster); // cache at epoch 3

        Assert.False(r.OnAnchorFailover(Cluster, "urn:nps:node:x:anchor-old", clusterEpoch: 3)); // equal → stale
        Assert.False(r.OnAnchorFailover(Cluster, "urn:nps:node:x:anchor-old", clusterEpoch: 2)); // lower → stale
        Assert.Equal("urn:nps:node:x:anchor-b", r.ResolveActive(Cluster)!.ActiveNid);
    }

    [Fact]
    public void Invalidate_forces_a_fresh_lookup()
    {
        var answers = new Queue<ClusterAnchorInfo>([new("urn:nps:node:x:anchor-a", 1), new("urn:nps:node:x:anchor-b", 2)]);
        var r = new ClusterDelegationResolver(_ => answers.Dequeue());

        Assert.Equal("urn:nps:node:x:anchor-a", r.ResolveActive(Cluster)!.ActiveNid);
        r.Invalidate(Cluster); // e.g. after NWP-ANCHOR-NOT-LEADER
        Assert.Equal("urn:nps:node:x:anchor-b", r.ResolveActive(Cluster)!.ActiveNid);
    }
}
