// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using NPS.NOP.Frames;

namespace NPS.NOP.Orchestration;

/// <summary>Resolution of a cluster-anchor NID to its current active Anchor (NPS-CR-0009).</summary>
public sealed record ClusterAnchorInfo(string ActiveNid, ulong ClusterEpoch);

/// <summary>
/// NOP v0.8: resolves <see cref="DelegateFrame.TargetClusterAnchor"/> to the cluster's
/// <b>current active</b> Anchor (highest <c>cluster_epoch</c>, NDP §9) and tracks
/// <c>anchor_failover</c> events so in-flight delegations re-resolve to the successor
/// before retry. The NDP lookup is injected as a delegate so <c>NPS.NOP</c> stays free of
/// an NDP dependency — pass <c>registry.ResolveCluster(...)</c> from the composition root.
/// </summary>
public sealed class ClusterDelegationResolver(Func<string, ClusterAnchorInfo?> resolveCluster)
{
    private readonly Func<string, ClusterAnchorInfo?> _resolveCluster =
        resolveCluster ?? throw new ArgumentNullException(nameof(resolveCluster));
    private readonly ConcurrentDictionary<string, ClusterAnchorInfo> _active = new(StringComparer.Ordinal);

    /// <summary>
    /// The dispatch target for <paramref name="frame"/>: the cluster's current active Anchor
    /// NID when <see cref="DelegateFrame.TargetClusterAnchor"/> is set (resolved via cache or
    /// the injected NDP lookup), otherwise <see cref="DelegateFrame.TargetAgentNid"/>.
    /// Returns null when a cluster target cannot be resolved (caller decides retry/fail).
    /// </summary>
    public string? ResolveDelegateTarget(DelegateFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrEmpty(frame.TargetClusterAnchor))
            return frame.TargetAgentNid;
        return ResolveActive(frame.TargetClusterAnchor)?.ActiveNid;
    }

    /// <summary>Cached-or-fresh resolution of a cluster's active Anchor.</summary>
    public ClusterAnchorInfo? ResolveActive(string clusterAnchor)
    {
        ArgumentException.ThrowIfNullOrEmpty(clusterAnchor);
        if (_active.TryGetValue(clusterAnchor, out var cached))
            return cached;
        var fresh = _resolveCluster(clusterAnchor);
        if (fresh is not null)
            _active[clusterAnchor] = fresh;
        return fresh;
    }

    /// <summary>
    /// Apply an NWP <c>anchor_failover</c> event (NPS-2 §12.2). Epochs are monotonic per
    /// cluster: an event at or below the cached epoch is stale and ignored (returns false).
    /// After a successful update, subsequent delegations to the cluster target the successor.
    /// </summary>
    public bool OnAnchorFailover(string clusterAnchor, string successorNid, ulong clusterEpoch)
    {
        ArgumentException.ThrowIfNullOrEmpty(clusterAnchor);
        ArgumentException.ThrowIfNullOrEmpty(successorNid);
        while (true)
        {
            if (_active.TryGetValue(clusterAnchor, out var cur))
            {
                if (clusterEpoch <= cur.ClusterEpoch) return false; // stale event
                if (_active.TryUpdate(clusterAnchor, new ClusterAnchorInfo(successorNid, clusterEpoch), cur))
                    return true;
            }
            else if (_active.TryAdd(clusterAnchor, new ClusterAnchorInfo(successorNid, clusterEpoch)))
            {
                return true;
            }
        }
    }

    /// <summary>Drop the cached entry (e.g. after NWP-ANCHOR-NOT-LEADER) to force a fresh NDP lookup.</summary>
    public void Invalidate(string clusterAnchor) => _active.TryRemove(clusterAnchor, out _);
}
