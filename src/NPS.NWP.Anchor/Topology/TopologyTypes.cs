// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NWP.Anchor.Topology;

// ── Snapshot ─────────────────────────────────────────────────────────────────

/// <summary>
/// Result of a <c>topology.snapshot</c> reserved query (NPS-2 §12.1). Returned
/// by <see cref="IAnchorTopologyService.GetSnapshotAsync"/> and serialized into
/// the single <c>data[0]</c> element of the response <c>CapsFrame</c>.
/// </summary>
public sealed record TopologySnapshot
{
    /// <summary>
    /// Monotonically increasing topology version (NPS-2 §12.3). A snapshot at
    /// version <c>V</c> reflects the cluster state after exactly <c>V</c>
    /// topology mutations on this Anchor.
    /// </summary>
    [JsonPropertyName("version")]
    public required ulong Version { get; init; }

    /// <summary>NID of the responding Anchor Node.</summary>
    [JsonPropertyName("anchor_nid")]
    public required string AnchorNid { get; init; }

    /// <summary>Total direct members, regardless of any depth truncation.</summary>
    [JsonPropertyName("cluster_size")]
    public required uint ClusterSize { get; init; }

    /// <summary>Per-member metadata; see <see cref="MemberInfo"/>.</summary>
    [JsonPropertyName("members")]
    public required IReadOnlyList<MemberInfo> Members { get; init; }

    /// <summary><c>true</c> iff <c>topology.depth</c> cap was hit; otherwise omitted or false.</summary>
    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }
}

/// <summary>
/// One member of an Anchor Node's cluster (NPS-2 §12.1 member object schema).
/// </summary>
public sealed record MemberInfo
{
    [JsonPropertyName("nid")]
    public required string Nid { get; init; }

    /// <summary>
    /// NDP <c>node_roles</c> values for this member (NPS-4 §3.1; renamed from
    /// <c>node_kind</c> by M1 / NPS-CR-0001; parsers MUST accept <c>node_kind</c>
    /// as alias through alpha.5). Always at least one entry.
    /// </summary>
    [JsonPropertyName("node_roles")]
    public required IReadOnlyList<string> NodeRoles { get; init; }

    /// <summary>
    /// NDP <c>activation_mode</c>: <c>ephemeral</c> / <c>resident</c> / <c>hybrid</c>.
    /// </summary>
    [JsonPropertyName("activation_mode")]
    public required string ActivationMode { get; init; }

    /// <summary>True if this member is itself an Anchor of a sub-cluster. Implies <see cref="MemberCount"/>.</summary>
    [JsonPropertyName("child_anchor")]
    public bool? ChildAnchor { get; init; }

    /// <summary>Required when <see cref="ChildAnchor"/> is true; sub-Anchor's direct member count.</summary>
    [JsonPropertyName("member_count")]
    public uint? MemberCount { get; init; }

    /// <summary>NDP-declared tags (free-form labels).</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("joined_at")]
    public string? JoinedAt { get; init; }

    [JsonPropertyName("last_seen")]
    public string? LastSeen { get; init; }

    /// <summary>Returned only if requested via <see cref="TopologyInclude.Capabilities"/>; schema implementation-defined.</summary>
    [JsonPropertyName("capabilities")]
    public JsonElement? Capabilities { get; init; }

    /// <summary>Returned only if requested via <see cref="TopologyInclude.Metrics"/>; schema implementation-defined.</summary>
    [JsonPropertyName("metrics")]
    public JsonElement? Metrics { get; init; }
}

// ── Request shapes ───────────────────────────────────────────────────────────

/// <summary>
/// <c>topology.scope</c> values from NPS-2 §12.1 / §12.2.
/// </summary>
public enum TopologyScope
{
    /// <summary>The Anchor's own cluster.</summary>
    Cluster,
    /// <summary>A single named member; requires <c>target_nid</c>.</summary>
    Member,
}

/// <summary>
/// Optional <c>topology.include</c> bit flags (NPS-2 §12.1). Default
/// <see cref="Members"/> alone matches the spec's default.
/// </summary>
[Flags]
public enum TopologyInclude
{
    None         = 0,
    Members      = 1 << 0,
    Capabilities = 1 << 1,
    Tags         = 1 << 2,
    Metrics      = 1 << 3,

    /// <summary>Convenience alias for the spec's default <c>["members"]</c>.</summary>
    Default = Members,
}

/// <summary>
/// Request parameters for <see cref="IAnchorTopologyService.GetSnapshotAsync"/>
/// (NPS-2 §12.1).
/// </summary>
public sealed record TopologySnapshotRequest
{
    public TopologyScope Scope     { get; init; } = TopologyScope.Cluster;
    public TopologyInclude Include { get; init; } = TopologyInclude.Default;
    public byte Depth              { get; init; } = 1;
    public string? TargetNid       { get; init; }
}

/// <summary>
/// Request parameters for <see cref="IAnchorTopologyService.SubscribeAsync"/>
/// (NPS-2 §12.2).
/// </summary>
public sealed record TopologyStreamRequest
{
    public TopologyScope Scope { get; init; } = TopologyScope.Cluster;
    public TopologyFilter? Filter { get; init; }

    /// <summary>
    /// Resume from the given version (synonym of <c>SubscribeFrame.resume_from_seq</c>).
    /// <c>null</c> means "from now". When the version is outside the Anchor's
    /// retention window, the first emitted event is <see cref="ResyncRequired"/>.
    /// </summary>
    public ulong? SinceVersion { get; init; }
}

/// <summary>
/// Subscriber-side filter (NPS-2 §12.2). All filter clauses are AND-combined;
/// a member matches if every populated clause matches.
/// </summary>
public sealed record TopologyFilter
{
    /// <summary>Match-any: member's tag set MUST overlap with this list.</summary>
    [JsonPropertyName("tags_any")]
    public IReadOnlyList<string>? TagsAny { get; init; }

    /// <summary>Match-all: member's tag set MUST be a superset of this list.</summary>
    [JsonPropertyName("tags_all")]
    public IReadOnlyList<string>? TagsAll { get; init; }

    /// <summary>Member's <c>node_roles</c> array MUST contain at least one of these values.</summary>
    [JsonPropertyName("node_roles")]
    public IReadOnlyList<string>? NodeRoles { get; init; }
}

// ── Stream events ────────────────────────────────────────────────────────────

/// <summary>
/// Base type for events pushed by <c>topology.stream</c> (NPS-2 §12.2). The
/// concrete subclass corresponds 1:1 to the wire <c>event_type</c>.
/// </summary>
public abstract record TopologyEvent
{
    /// <summary>
    /// Post-event topology version. <c>0</c> for <see cref="ResyncRequired"/>
    /// (the only event that omits a version per §12.2).
    /// </summary>
    public ulong Version { get; init; }
}

public sealed record MemberJoined : TopologyEvent
{
    public required MemberInfo Member { get; init; }
}

public sealed record MemberLeft : TopologyEvent
{
    public required string Nid { get; init; }
}

/// <summary>
/// Field-level diff per CR-0002 §10 OQ-2 (default proposal: changes object,
/// not full object). The subscriber reassembles the post-state.
/// </summary>
public sealed record MemberUpdated : TopologyEvent
{
    public required string Nid { get; init; }
    public required MemberChanges Changes { get; init; }
}

/// <summary>
/// Subset of <see cref="MemberInfo"/> fields the Anchor reports as changed.
/// All members are nullable; only populated fields contribute to the diff.
/// </summary>
public sealed record MemberChanges
{
    [JsonPropertyName("node_roles")]
    public IReadOnlyList<string>? NodeRoles { get; init; }
    public string? ActivationMode { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public uint? MemberCount { get; init; }
    public string? LastSeen { get; init; }
    public JsonElement? Capabilities { get; init; }
    public JsonElement? Metrics { get; init; }
}

public sealed record AnchorState : TopologyEvent
{
    /// <summary>Field tag, e.g. <c>"version_rebased"</c> (NPS-2 §12.3 restart-and-rebase).</summary>
    public required string Field { get; init; }
    public JsonElement? Details { get; init; }
}

/// <summary>
/// Emitted when the subscriber's <c>since_version</c> is no longer replayable.
/// Carries no <c>seq</c>; subscriber MUST issue a fresh <c>topology.snapshot</c>.
/// </summary>
public sealed record ResyncRequired : TopologyEvent
{
    public required string Reason { get; init; }
}

// ── Wire payload helpers ─────────────────────────────────────────────────────

/// <summary>
/// On-the-wire shape for one <c>topology.stream</c> event (DiffFrame extension
/// fields per NPS-2 §8.2 + §12.2). Used by both server and client when
/// (de)serializing NDJSON-streamed event lines.
/// </summary>
internal sealed record TopologyEventEnvelope
{
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    [JsonPropertyName("seq")]
    public ulong? Seq { get; init; }   // null only for resync_required

    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }

    /// <summary>
    /// Estimated CGN cost of this event's payload (UTF-8/4 fallback per
    /// token-budget §3, §7.2). Consumers MAY use this to track running
    /// budget consumption across a <c>topology.stream</c> session.
    /// </summary>
    [JsonPropertyName("cgn_est")]
    public uint? CgnEst { get; init; }
}

/// <summary>
/// First line emitted on <c>/subscribe</c> — the spec's "subscription
/// acknowledgement CapsFrame" rendered as a stream chunk for the NDJSON
/// transport. Distinguished from event lines by <c>kind: "ack"</c>.
/// </summary>
internal sealed record TopologySubscribeAck
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "ack";

    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }   // "subscribed"

    [JsonPropertyName("last_seq")]
    public required ulong LastSeq { get; init; }

    [JsonPropertyName("resumed")]
    public required bool Resumed { get; init; }
}

// ── Wire constants ───────────────────────────────────────────────────────────

/// <summary>String constants used on the wire for reserved query types and event types (NPS-2 §12).</summary>
public static class TopologyWire
{
    public const string TypeSnapshot = "topology.snapshot";
    public const string TypeStream   = "topology.stream";

    public const string ScopeCluster = "cluster";
    public const string ScopeMember  = "member";

    public const string EventMemberJoined   = "member_joined";
    public const string EventMemberLeft     = "member_left";
    public const string EventMemberUpdated  = "member_updated";
    public const string EventAnchorState    = "anchor_state";
    public const string EventResyncRequired = "resync_required";

    public const string SnapshotAnchorRef = "nps:system:topology:snapshot";

    public const string IncludeMembers      = "members";
    public const string IncludeCapabilities = "capabilities";
    public const string IncludeTags         = "tags";
    public const string IncludeMetrics      = "metrics";

    public const string AnchorStateVersionRebased = "version_rebased";
}
