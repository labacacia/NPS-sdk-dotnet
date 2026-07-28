// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using MessagePack;
using NPS.Core.Frames;

namespace NPS.NDP.Frames;

// ── Supporting value types ────────────────────────────────────────────────────

/// <summary>
/// Physical address entry inside an <see cref="AnnounceFrame"/> (NPS-4 §3.1).
/// Each node may publish multiple addresses (IPv4, IPv6, hostname).
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record NdpAddress
{
    /// <summary>Hostname or IP (e.g. <c>"10.0.0.5"</c> or <c>"api.example.com"</c>).</summary>
    [JsonPropertyName("host")]
    public required string Host { get; init; }

    /// <summary>TCP port (e.g. <c>17434</c>).</summary>
    [JsonPropertyName("port")]
    public required int Port { get; init; }

    /// <summary>Protocol identifier, e.g. <c>"nwp"</c> or <c>"nwp+tls"</c>.</summary>
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }
}

/// <summary>
/// Resolved endpoint returned inside a <see cref="ResolveFrame"/> response (NPS-4 §3.2).
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record NdpResolveResult
{
    /// <summary>Resolved hostname or IP.</summary>
    [JsonPropertyName("host")]
    public required string Host { get; init; }

    /// <summary>Resolved TCP port.</summary>
    [JsonPropertyName("port")]
    public required int Port { get; init; }

    /// <summary>Optional certificate fingerprint in <c>sha256:{hex}</c> format.</summary>
    [JsonPropertyName("cert_fingerprint")]
    public string? CertFingerprint { get; init; }

    /// <summary>Time-to-live for this resolution result (seconds).</summary>
    [JsonPropertyName("ttl")]
    public required uint Ttl { get; init; }
}

/// <summary>
/// A node entry in a <see cref="GraphFrame"/> topology snapshot (NPS-4 §3.3, NDP v0.8).
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record NdpGraphNode
{
    /// <summary>Node NID.</summary>
    [JsonPropertyName("nid")]
    public required string Nid { get; init; }

    /// <summary>NID of this node's cluster Anchor, when part of a multi-cluster topology.</summary>
    [JsonPropertyName("cluster_anchor")]
    public string? ClusterAnchor { get; init; }

    /// <summary>Node role set, e.g. <c>["memory"]</c> or <c>["action", "bridge"]</c>.</summary>
    [JsonPropertyName("node_roles")]
    public IReadOnlyList<string>? NodeRoles { get; init; }
}

/// <summary>
/// A directed edge in a <see cref="GraphFrame"/> topology snapshot (NPS-4 §3.3, NDP v0.8).
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record NdpGraphEdge
{
    /// <summary>Source node NID.</summary>
    [JsonPropertyName("from_nid")]
    public required string FromNid { get; init; }

    /// <summary>Destination node NID.</summary>
    [JsonPropertyName("to_nid")]
    public required string ToNid { get; init; }

    /// <summary>Observed round-trip latency in milliseconds (informational).</summary>
    [JsonPropertyName("latency_ms")]
    public uint? LatencyMs { get; init; }

    /// <summary>Transport protocol used on this edge, e.g. <c>"ncp"</c>, <c>"https"</c>.</summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }
}

// ── AnnounceFrame (0x30) ──────────────────────────────────────────────────────

/// <summary>
/// Node or Agent presence broadcast (NPS-4 §3.1).
/// Sent to announce online status, physical addresses, and capabilities.
/// A <c>ttl</c> of <c>0</c> signals an orderly shutdown (offline announcement).
///
/// <para>The <c>signature</c> field MUST be an Ed25519 signature over the canonical
/// JSON of the frame (minus <c>signature</c>), made with the publisher's own private key
/// (the same key that produced the corresponding <see cref="NPS.NIP.Frames.IdentFrame"/>).</para>
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record AnnounceFrame : IFrame
{
    /// <inheritdoc/>
    [JsonIgnore, IgnoreMember] public FrameType FrameType => FrameType.Announce;
    /// <inheritdoc/>
    [JsonIgnore, IgnoreMember] public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Frame type discriminant. Fixed value <c>"0x30"</c>.</summary>
    [JsonPropertyName("frame")]
    public string Frame { get; init; } = "0x30";

    /// <summary>Publisher NID, e.g. <c>urn:nps:node:api.example.com:products</c>.</summary>
    [JsonPropertyName("nid")]
    public required string Nid { get; init; }

    /// <summary>
    /// Node type. Required when the publisher is a node entity
    /// (e.g. <c>"memory"</c>, <c>"action"</c>, <c>"complex"</c>, <c>"gateway"</c>).
    /// Omit for Agent announcements.
    /// </summary>
    [JsonPropertyName("node_type")]
    public string? NodeType { get; init; }

    /// <summary>Physical addresses where the publisher can be reached.</summary>
    [JsonPropertyName("addresses")]
    public required IReadOnlyList<NdpAddress> Addresses { get; init; }

    /// <summary>Capabilities the publisher offers, e.g. <c>["nwp:query", "nwp:stream"]</c>.</summary>
    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    /// Time-to-live in seconds. <c>0</c> = offline / shutdown notification.
    /// Receivers SHOULD evict this entry from their registry when TTL expires.
    /// </summary>
    [JsonPropertyName("ttl")]
    public required uint Ttl { get; init; }

    /// <summary>
    /// How often this node will re-announce itself (milliseconds). Defaults to 60000.
    /// Included in the signed AnnounceFrame canonical body; <see cref="Health"/> and
    /// <see cref="LastSeen"/> remain wire-only liveness updates.
    /// </summary>
    [JsonPropertyName("heartbeat_interval_ms")]
    public uint HeartbeatIntervalMs { get; init; } = 60_000;

    /// <summary>Announcement timestamp (ISO 8601 UTC).</summary>
    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    /// <summary>
    /// Ed25519 signature (<c>ed25519:{base64url}</c>) over the canonical JSON of this
    /// frame (minus the <c>signature</c> field), produced with the publisher's private key.
    /// </summary>
    [JsonPropertyName("signature")]
    public required string Signature { get; init; }

    // ── NPS-4 v0.7 fields ─────────────────────────────────────────────────────

    /// <summary>
    /// Lifecycle model of this node (NPS-4 §3.1.1):
    /// <c>"ephemeral"</c> — short-lived, evicted quickly;
    /// <c>"resident"</c> — persistent long-running service (default);
    /// <c>"hybrid"</c> — resident base that can spin up ephemeral instances.
    /// Receivers cap effective TTL per activation_mode (§3.1.1).
    /// </summary>
    [JsonPropertyName("activation_mode")]
    public string? ActivationMode { get; init; }

    /// <summary>
    /// Semantic roles this node fulfils, e.g. <c>["anchor","memory"]</c> (NPS-4 §3.1).
    /// Used by consumers to filter nodes by function rather than capability.
    /// </summary>
    [JsonPropertyName("node_roles")]
    public IReadOnlyList<string>? NodeRoles { get; init; }

    /// <summary>
    /// NID of the cluster anchor that coordinates this node's cluster (NPS-4 §3.1).
    /// Present only for nodes that are members of a named cluster.
    /// </summary>
    [JsonPropertyName("cluster_anchor")]
    public string? ClusterAnchor { get; init; }

    /// <summary>
    /// URI reference to a machine-readable spawn specification (NPS-4 §3.1).
    /// Consumers can fetch this spec to understand how to launch a new instance.
    /// </summary>
    [JsonPropertyName("spawn_spec_ref")]
    public string? SpawnSpecRef { get; init; }

    /// <summary>
    /// Additional bridging protocols supported by this node beyond the primary address
    /// list, e.g. <c>["a2a","mcp"]</c> (NPS-4 §3.1).
    /// </summary>
    [JsonPropertyName("bridge_protocols")]
    public IReadOnlyList<string>? BridgeProtocols { get; init; }

    /// <summary>
    /// External protocols this Bridge Node <b>serves inbound</b> (external → NPS), e.g.
    /// <c>["mcp","grpc"]</c> — protocols for which it exposes a native server endpoint.
    /// Same value domain as <see cref="BridgeProtocols"/> (the outbound set). Absent or empty ⇒
    /// no inbound surface, i.e. an outbound-only Bridge Node (the only kind that existed through
    /// alpha.15). A node declaring the <c>"bridge"</c> role MUST have at least one of the two
    /// non-empty (NPS-4 §3.1, NPS-CR-0010).
    /// </summary>
    [JsonPropertyName("bridge_inbound_protocols")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? BridgeInboundProtocols { get; init; }

    /// <summary>
    /// Push target at which a resident or hybrid publisher accepts activation traffic.
    /// Same shape as an <see cref="NdpAddress"/> entry in <see cref="Addresses"/> (NPS-4 §3.1).
    /// </summary>
    [JsonPropertyName("activation_endpoint")]
    public NdpAddress? ActivationEndpoint { get; init; }

    /// <summary>
    /// Publisher liveness self-report (NDP v0.9). One of <c>"healthy"</c> / <c>"degraded"</c> /
    /// <c>"draining"</c>. Absent ⇒ receivers treat as <c>"healthy"</c>. <c>"draining"</c> signals
    /// the node is shutting down and SHOULD NOT receive new traffic (NPS-4 §3.1).
    /// </summary>
    [JsonPropertyName("health")]
    public string? Health { get; init; }

    /// <summary>
    /// ISO 8601 UTC timestamp of the publisher's most recent liveness beat (NDP v0.9). When
    /// present, a Registry uses <c>last_seen + ttl</c> (rather than <c>timestamp + ttl</c>) as the
    /// resolve-time freshness deadline (NPS-4 §3.2.1). Absent ⇒ fall back to <c>timestamp</c>.
    /// </summary>
    [JsonPropertyName("last_seen")]
    public string? LastSeen { get; init; }

    /// <summary>
    /// Multi-Anchor cluster ownership fence (NPS-CR-0009). The epoch under which this Anchor
    /// holds ownership of its <see cref="ClusterAnchor"/> cluster; strictly increases on each
    /// ownership transfer. Absent (<c>null</c>) is treated as <c>1</c> (single-Anchor). Omitted
    /// from the signed canonical body when null, so single-Anchor frames are unaffected.
    /// </summary>
    [JsonPropertyName("cluster_epoch")]
    public ulong? ClusterEpoch { get; init; }
}

// ── ResolveFrame (0x31) ───────────────────────────────────────────────────────

/// <summary>
/// Resolve a <c>nwp://</c> URL to a physical endpoint (NPS-4 §3.2).
///
/// <para>Used as both request and response:</para>
/// <list type="bullet">
///   <item>Request: populate <see cref="Target"/> (and optionally <see cref="RequesterNid"/>).</item>
///   <item>Response: same <see cref="Target"/> plus a populated <see cref="Resolved"/> object.</item>
/// </list>
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ResolveFrame : IFrame
{
    /// <inheritdoc/>
    [JsonIgnore, IgnoreMember] public FrameType FrameType => FrameType.Resolve;
    /// <inheritdoc/>
    [JsonIgnore, IgnoreMember] public EncodingTier PreferredTier => EncodingTier.Json;

    /// <summary>Frame type discriminant. Fixed value <c>"0x31"</c>.</summary>
    [JsonPropertyName("frame")]
    public string Frame { get; init; } = "0x31";

    /// <summary>The <c>nwp://</c> URL to resolve, e.g. <c>"nwp://api.example.com/products"</c>.</summary>
    [JsonPropertyName("target")]
    public required string Target { get; init; }

    /// <summary>Requesting party NID for optional authorisation checks. May be null.</summary>
    [JsonPropertyName("requester_nid")]
    public string? RequesterNid { get; init; }

    /// <summary>
    /// Populated in the response; null in a request.
    /// Contains the resolved physical endpoint.
    /// </summary>
    [JsonPropertyName("resolved")]
    public NdpResolveResult? Resolved { get; init; }
}

// ── GraphFrame (0x32) ─────────────────────────────────────────────────────────

/// <summary>
/// Multi-cluster topology graph snapshot (NPS-4 §3.3, NDP v0.8).
/// Published by Registries or Anchor Nodes to describe inter-cluster connectivity.
/// Max 256 nodes, max 1024 edges per frame.
/// Oversized frames MUST be rejected with <c>NDP-GRAPH-TOO-LARGE</c>;
/// structural errors with <c>NDP-GRAPH-INVALID</c>.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record GraphFrame : IFrame
{
    /// <inheritdoc/>
    [JsonIgnore, IgnoreMember] public FrameType FrameType => FrameType.Graph;
    /// <inheritdoc/>
    [JsonIgnore, IgnoreMember] public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Frame type discriminant. Fixed value <c>"0x32"</c>.</summary>
    [JsonPropertyName("frame")]
    public string Frame { get; init; } = "0x32";

    /// <summary>Opaque snapshot identifier assigned by the publisher.</summary>
    [JsonPropertyName("graph_id")]
    public required string GraphId { get; init; }

    /// <summary>Node entries in this snapshot. Maximum 256.</summary>
    [JsonPropertyName("nodes")]
    public required IReadOnlyList<NdpGraphNode> Nodes { get; init; }

    /// <summary>Directed edges describing inter-node connectivity. Maximum 1024.</summary>
    [JsonPropertyName("edges")]
    public required IReadOnlyList<NdpGraphEdge> Edges { get; init; }

    /// <summary>Time-to-live for this snapshot in seconds. 0 means no expiry.</summary>
    [JsonPropertyName("ttl")]
    public required uint Ttl { get; init; }

    /// <summary>Free-form publisher metadata (informational only).</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }

    /// <summary>Validate NPS-4 §3.3 GraphFrame bounds and edge structure.</summary>
    public void Validate()
    {
        const int maxGraphNodes = 256;
        const int maxGraphEdges = 1024;

        if (Nodes is null || Edges is null)
            throw new ArgumentException($"{NPS.NDP.NdpErrorCodes.GraphInvalid}: nodes and edges are required");
        if (Nodes.Count > maxGraphNodes)
            throw new ArgumentException($"{NPS.NDP.NdpErrorCodes.GraphTooLarge}: nodes length exceeds {maxGraphNodes}");
        if (Edges.Count > maxGraphEdges)
            throw new ArgumentException($"{NPS.NDP.NdpErrorCodes.GraphTooLarge}: edges length exceeds {maxGraphEdges}");

        var nodeIds = Nodes.Select(node => node.Nid).ToHashSet(StringComparer.Ordinal);
        if (Nodes.Any(node => string.IsNullOrEmpty(node.Nid)))
            throw new ArgumentException($"{NPS.NDP.NdpErrorCodes.GraphInvalid}: graph nodes require nid");

        foreach (var edge in Edges)
        {
            if (string.IsNullOrEmpty(edge.FromNid) || string.IsNullOrEmpty(edge.ToNid))
                throw new ArgumentException(
                    $"{NPS.NDP.NdpErrorCodes.GraphInvalid}: graph edges require from_nid and to_nid");
            if (edge.FromNid == edge.ToNid)
                throw new ArgumentException($"{NPS.NDP.NdpErrorCodes.GraphInvalid}: graph self-edges are forbidden");
            if (!nodeIds.Contains(edge.FromNid) || !nodeIds.Contains(edge.ToNid))
                throw new ArgumentException(
                    $"{NPS.NDP.NdpErrorCodes.GraphInvalid}: graph edge endpoints must appear in nodes");
        }
    }
}
