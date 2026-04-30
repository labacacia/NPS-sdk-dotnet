// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace NPS.NWP.Anchor.Topology;

/// <summary>
/// Thread-safe in-memory <see cref="IAnchorTopologyService"/>. Keeps a single
/// member map and a bounded ring buffer of recent events for
/// <c>topology.stream</c> replay.
///
/// <para>This is the <b>reference</b> implementation that ships with
/// <see cref="NPS.NWP.Anchor"/>; it is sufficient for single-node deployments,
/// the L2 conformance suite, and tests. Production multi-node deployments
/// SHOULD plug in an implementation backed by a distributed store and route
/// NDP <c>Announce</c> ingestion through their own pipeline.</para>
///
/// <para>External code drives state changes through
/// <see cref="MemberJoined(string, IReadOnlyList{string}, string, IReadOnlyList{string}?, bool, uint?)"/>,
/// <see cref="MemberLeft(string)"/>, and
/// <see cref="MemberUpdated(string, MemberChanges)"/>. The version counter is
/// strictly monotonic across the service's lifetime; a process restart causes
/// callers (npsd / similar) to invoke <see cref="RebaseVersion"/> after
/// reloading state from durable storage so subscribers receive an
/// <see cref="AnchorState"/> rebase event rather than silently observing a
/// counter regression.</para>
/// </summary>
public sealed class InMemoryAnchorTopologyService : IAnchorTopologyService
{
    /// <summary>How many recent events to retain for <c>since_version</c> replay.</summary>
    public const int DefaultRetention = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, MemberInfo> _members = new(StringComparer.Ordinal);
    private readonly LinkedList<RetainedEvent> _retained = new();
    private readonly List<Subscriber> _subscribers = new();

    private ulong _version;
    private readonly int _retention;

    public InMemoryAnchorTopologyService(string anchorNid, int retention = DefaultRetention)
    {
        if (string.IsNullOrWhiteSpace(anchorNid))
            throw new ArgumentException("anchorNid must be non-empty.", nameof(anchorNid));
        if (retention <= 0)
            throw new ArgumentOutOfRangeException(nameof(retention));

        AnchorNid  = anchorNid;
        _retention = retention;
    }

    public string AnchorNid { get; }

    /// <summary>Most recent topology version (test helper; production callers should not depend on this).</summary>
    public ulong CurrentVersion
    {
        get { lock (_gate) return _version; }
    }

    /// <summary>
    /// Number of currently-attached <c>topology.stream</c> subscribers.
    /// Test fixtures use this to wait for a subscription to register
    /// before mutating state, eliminating the race that otherwise drops
    /// the very first event a brand-new subscriber expects.
    /// </summary>
    public int SubscriberCount
    {
        get { lock (_gate) return _subscribers.Count; }
    }

    // ── State mutators ───────────────────────────────────────────────────────

    /// <summary>
    /// Record a new member joining the cluster. Idempotent — re-announcing an
    /// existing NID surfaces as <see cref="MemberUpdated"/> instead of
    /// duplicating the join.
    /// </summary>
    public ulong MemberJoinedAnnounce(MemberInfo member)
    {
        if (member is null) throw new ArgumentNullException(nameof(member));

        TopologyEvent ev;
        ulong v;
        lock (_gate)
        {
            if (_members.TryGetValue(member.Nid, out var existing))
            {
                var changes = DiffMembers(existing, member);
                if (changes is null) return _version;   // no-op
                _members[member.Nid] = MergeChanges(existing, member);
                v = ++_version;
                ev = new MemberUpdated { Version = v, Nid = member.Nid, Changes = changes };
            }
            else
            {
                _members[member.Nid] = member;
                v = ++_version;
                ev = new MemberJoined { Version = v, Member = member };
            }
            Retain(v, ev);
        }
        FanOut(ev);
        return v;
    }

    /// <summary>
    /// Convenience overload for callers that don't need to construct a full
    /// <see cref="MemberInfo"/> upfront.
    /// </summary>
    public ulong MemberJoinedAnnounce(
        string nid,
        IReadOnlyList<string> nodeKind,
        string activationMode,
        IReadOnlyList<string>? tags = null,
        bool childAnchor = false,
        uint? memberCount = null,
        string? joinedAt = null)
    {
        return MemberJoinedAnnounce(new MemberInfo
        {
            Nid            = nid,
            NodeKind       = nodeKind,
            ActivationMode = activationMode,
            Tags           = tags,
            ChildAnchor    = childAnchor ? true : null,
            MemberCount    = memberCount,
            JoinedAt       = joinedAt ?? DateTimeOffset.UtcNow.ToString("O"),
            LastSeen       = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    /// <summary>Record a member leaving (TTL expiry, explicit shutdown, etc.).</summary>
    public ulong MemberLeftAnnounce(string nid)
    {
        TopologyEvent ev;
        ulong v;
        lock (_gate)
        {
            if (!_members.Remove(nid)) return _version;
            v = ++_version;
            ev = new MemberLeft { Version = v, Nid = nid };
            Retain(v, ev);
        }
        FanOut(ev);
        return v;
    }

    /// <summary>Apply a partial update to an existing member's metadata.</summary>
    public ulong MemberUpdatedAnnounce(string nid, MemberChanges changes)
    {
        TopologyEvent ev;
        ulong v;
        lock (_gate)
        {
            if (!_members.TryGetValue(nid, out var existing)) return _version;
            _members[nid] = ApplyChanges(existing, changes);
            v = ++_version;
            ev = new MemberUpdated { Version = v, Nid = nid, Changes = changes };
            Retain(v, ev);
        }
        FanOut(ev);
        return v;
    }

    /// <summary>
    /// Restart-recovery hook (NPS-2 §12.3 restart-and-rebase). The caller
    /// supplies the version it last persisted; this service rebases its
    /// counter forward from there and emits an
    /// <see cref="AnchorState"/> event with field
    /// <c>version_rebased</c> so existing subscribers know to re-snapshot.
    /// </summary>
    public ulong RebaseVersion(ulong newBase)
    {
        TopologyEvent ev;
        ulong v;
        lock (_gate)
        {
            if (newBase < _version)
                throw new InvalidOperationException("rebase target must not regress.");
            _version = newBase + 1;
            v = _version;
            ev = new AnchorState
            {
                Version = v,
                Field   = TopologyWire.AnchorStateVersionRebased,
                Details = null,
            };
            Retain(v, ev);
        }
        FanOut(ev);
        return v;
    }

    // ── IAnchorTopologyService ───────────────────────────────────────────────

    public Task<TopologySnapshot> GetSnapshotAsync(
        TopologySnapshotRequest request,
        CancellationToken       cancel = default)
    {
        if (request.Scope == TopologyScope.Member && string.IsNullOrEmpty(request.TargetNid))
            throw new TopologyProtocolException(
                NwpTopologyErrorCodes.UnsupportedScope, "NPS-CLIENT-BAD-PARAM",
                "topology.target_nid is required when topology.scope = \"member\".");

        lock (_gate)
        {
            IEnumerable<MemberInfo> source = _members.Values;
            if (request.Scope == TopologyScope.Member)
                source = source.Where(m => m.Nid == request.TargetNid);

            var snapshot = new TopologySnapshot
            {
                Version     = _version,
                AnchorNid   = AnchorNid,
                ClusterSize = (uint)_members.Count,
                Members     = source.Select(m => ProjectMember(m, request.Include)).ToArray(),
                Truncated   = null,
            };
            return Task.FromResult(snapshot);
        }
    }

    public async IAsyncEnumerable<TopologyEvent> SubscribeAsync(
        TopologyStreamRequest                       request,
        [EnumeratorCancellation] CancellationToken  cancel = default)
    {
        ValidateFilter(request.Filter);

        var ch = Channel.CreateUnbounded<TopologyEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var subscriber = new Subscriber(ch, request.Filter);
        bool resyncFirst = false;
        IReadOnlyList<TopologyEvent>? replay = null;

        lock (_gate)
        {
            if (request.SinceVersion is { } since)
            {
                if (since > _version)
                {
                    // Caller's reference is "from the future" — treat as
                    // resync rather than wait for events that may never come.
                    resyncFirst = true;
                }
                else if (_retained.Count == 0 || _retained.First!.Value.Version > since + 1)
                {
                    resyncFirst = true;
                }
                else
                {
                    replay = _retained
                        .Where(re => re.Version > since)
                        .Select(re => re.Event)
                        .ToArray();
                }
            }

            if (!resyncFirst)
                _subscribers.Add(subscriber);
        }

        try
        {
            if (resyncFirst)
            {
                yield return new ResyncRequired { Version = 0, Reason = "version_too_old" };
                yield break;
            }

            if (replay is { Count: > 0 })
            {
                foreach (var ev in replay)
                {
                    if (cancel.IsCancellationRequested) yield break;
                    if (Matches(ev, subscriber.Filter)) yield return ev;
                }
            }

            while (await ch.Reader.WaitToReadAsync(cancel).ConfigureAwait(false))
            {
                while (ch.Reader.TryRead(out var ev))
                {
                    if (cancel.IsCancellationRequested) yield break;
                    if (Matches(ev, subscriber.Filter)) yield return ev;
                }
            }
        }
        finally
        {
            lock (_gate) _subscribers.Remove(subscriber);
            ch.Writer.TryComplete();
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void Retain(ulong v, TopologyEvent ev)
    {
        _retained.AddLast(new RetainedEvent(v, ev));
        while (_retained.Count > _retention)
            _retained.RemoveFirst();
    }

    private void FanOut(TopologyEvent ev)
    {
        // Snapshot subscriber list under the lock, then dispatch outside
        // to avoid blocking writers if a channel back-pressures.
        Subscriber[] subs;
        lock (_gate) subs = _subscribers.ToArray();

        foreach (var s in subs)
        {
            // Best-effort write; unbounded channel means this rarely fails
            // unless the channel was completed by an unsubscribe.
            s.Channel.Writer.TryWrite(ev);
        }
    }

    private static MemberInfo ProjectMember(MemberInfo source, TopologyInclude include)
    {
        // The default include is Members which means "the whole member object,
        // minus capabilities/metrics unless asked". Tags travel by default
        // because they are inexpensive and the spec example shows them
        // unconditionally — matching reference behavior to the example
        // avoids a class of "where did my tags go" surprises.
        return source with
        {
            Capabilities = include.HasFlag(TopologyInclude.Capabilities) ? source.Capabilities : null,
            Metrics      = include.HasFlag(TopologyInclude.Metrics)      ? source.Metrics      : null,
        };
    }

    private static MemberChanges? DiffMembers(MemberInfo before, MemberInfo after)
    {
        var changes = new MemberChanges
        {
            NodeKind       = !ListEquals(before.NodeKind, after.NodeKind) ? after.NodeKind : null,
            ActivationMode = before.ActivationMode != after.ActivationMode ? after.ActivationMode : null,
            Tags           = !ListEquals(before.Tags, after.Tags) ? after.Tags : null,
            MemberCount    = before.MemberCount != after.MemberCount ? after.MemberCount : null,
            LastSeen       = before.LastSeen != after.LastSeen ? after.LastSeen : null,
        };
        bool hasAny =
            changes.NodeKind       is not null ||
            changes.ActivationMode is not null ||
            changes.Tags           is not null ||
            changes.MemberCount    is not null ||
            changes.LastSeen       is not null;
        return hasAny ? changes : null;
    }

    private static MemberInfo MergeChanges(MemberInfo before, MemberInfo update) => before with
    {
        NodeKind       = update.NodeKind,
        ActivationMode = update.ActivationMode,
        Tags           = update.Tags,
        MemberCount    = update.MemberCount,
        LastSeen       = update.LastSeen,
        ChildAnchor    = update.ChildAnchor ?? before.ChildAnchor,
        Capabilities   = update.Capabilities ?? before.Capabilities,
        Metrics        = update.Metrics      ?? before.Metrics,
    };

    private static MemberInfo ApplyChanges(MemberInfo before, MemberChanges c) => before with
    {
        NodeKind       = c.NodeKind       ?? before.NodeKind,
        ActivationMode = c.ActivationMode ?? before.ActivationMode,
        Tags           = c.Tags           ?? before.Tags,
        MemberCount    = c.MemberCount    ?? before.MemberCount,
        LastSeen       = c.LastSeen       ?? before.LastSeen,
        Capabilities   = c.Capabilities   ?? before.Capabilities,
        Metrics        = c.Metrics        ?? before.Metrics,
    };

    private static bool ListEquals<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i])) return false;
        return true;
    }

    private static void ValidateFilter(TopologyFilter? f)
    {
        // CR-0002 §3.5: unsupported filter keys MUST produce
        // NWP-TOPOLOGY-FILTER-UNSUPPORTED. Our reference accepts every
        // documented key (tags_any / tags_all / node_kind), so this method
        // is a hook for future implementations to short-circuit unknown keys
        // before they reach the matcher.
        _ = f;
    }

    private static bool Matches(TopologyEvent ev, TopologyFilter? filter)
    {
        if (filter is null) return true;

        return ev switch
        {
            MemberJoined j  => MatchesMember(j.Member, filter),
            MemberLeft       => true,                                  // leaves always pass; clients want to see them
            MemberUpdated    => true,                                  // updates always pass; client may already track
            AnchorState      => true,
            ResyncRequired   => true,
            _                => true,
        };
    }

    private static bool MatchesMember(MemberInfo m, TopologyFilter f)
    {
        if (f.NodeKind is { Count: > 0 } nk &&
            !nk.Any(k => m.NodeKind.Contains(k)))
            return false;

        if (f.TagsAny is { Count: > 0 } any)
        {
            if (m.Tags is null || !any.Any(t => m.Tags.Contains(t)))
                return false;
        }

        if (f.TagsAll is { Count: > 0 } all)
        {
            if (m.Tags is null || !all.All(t => m.Tags.Contains(t)))
                return false;
        }

        return true;
    }

    private readonly record struct RetainedEvent(ulong Version, TopologyEvent Event);

    private sealed class Subscriber
    {
        public Subscriber(Channel<TopologyEvent> channel, TopologyFilter? filter)
        {
            Channel = channel;
            Filter  = filter;
        }

        public Channel<TopologyEvent> Channel { get; }
        public TopologyFilter? Filter { get; }
    }
}
