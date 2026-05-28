// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Frames;

namespace NPS.NDP.Frames;

// ── Supporting value types ────────────────────────────────────────────────────

/// <summary>
/// Physical address entry inside an <see cref="AnnounceFrame"/> (NPS-4 §3.1).
/// Each node may publish multiple addresses (IPv4, IPv6, hostname).
/// </summary>
public sealed record NdpAddress
{
    /// <summary>Hostname or IP (e.g. <c>"10.0.0.5"</c> or <c>"api.example.com"</c>).</summary>
    public required string Host { get; init; }

    /// <summary>TCP port (e.g. <c>17434</c>).</summary>
    public required int Port { get; init; }

    /// <summary>Protocol identifier, e.g. <c>"nwp"</c> or <c>"nwp+tls"</c>.</summary>
    public required string Protocol { get; init; }
}

/// <summary>
/// Resolved endpoint returned inside a <see cref="ResolveFrame"/> response (NPS-4 §3.2).
/// </summary>
public sealed record NdpResolveResult
{
    /// <summary>Resolved hostname or IP.</summary>
    public required string Host { get; init; }

    /// <summary>Resolved TCP port.</summary>
    public required int Port { get; init; }

    /// <summary>Optional certificate fingerprint in <c>sha256:{hex}</c> format.</summary>
    [JsonPropertyName("cert_fingerprint")]
    public string? CertFingerprint { get; init; }

    /// <summary>Time-to-live for this resolution result (seconds).</summary>
    public required uint Ttl { get; init; }
}

/// <summary>
/// A node entry in a <see cref="GraphFrame"/> topology snapshot (NPS-4 §5, NDP v0.8).
/// </summary>
public sealed record NdpGraphNode
{
    /// <summary>Node NID.</summary>
    public required string Nid { get; init; }

    /// <summary>NID of this node's cluster Anchor, when part of a multi-cluster topology.</summary>
    [JsonPropertyName("cluster_anchor")]
    public string? ClusterAnchor { get; init; }

    /// <summary>Node role set, e.g. <c>["memory"]</c> or <c>["action", "bridge"]</c>.</summary>
    [JsonPropertyName("node_roles")]
    public IReadOnlyList<string>? NodeRoles { get; init; }
}

/// <summary>
/// A directed edge in a <see cref="GraphFrame"/> topology snapshot (NPS-4 §5, NDP v0.8).
/// </summary>
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
public sealed record AnnounceFrame : IFrame
{
    /// <inheritdoc/>
    [JsonIgnore] public FrameType    FrameType     => FrameType.Announce;
    /// <inheritdoc/>
    [JsonIgnore] public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Frame type discriminant. Fixed value <c>"0x30"</c>.</summary>
    [JsonPropertyName("frame")]
    public string Frame { get; init; } = "0x30";

    /// <summary>Publisher NID, e.g. <c>urn:nps:node:api.example.com:products</c>.</summary>
    public required string Nid { get; init; }

    /// <summary>
    /// Node type. Required when the publisher is a node entity
    /// (e.g. <c>"memory"</c>, <c>"action"</c>, <c>"complex"</c>, <c>"gateway"</c>).
    /// Omit for Agent announcements.
    /// </summary>
    [JsonPropertyName("node_type")]
    public string? NodeType { get; init; }

    /// <summary>Physical addresses where the publisher can be reached.</summary>
    public required IReadOnlyList<NdpAddress> Addresses { get; init; }

    /// <summary>Capabilities the publisher offers, e.g. <c>["nwp:query", "nwp:stream"]</c>.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    /// Time-to-live in seconds. <c>0</c> = offline / shutdown notification.
    /// Receivers SHOULD evict this entry from their registry when TTL expires.
    /// </summary>
    public required uint Ttl { get; init; }

    /// <summary>Announcement timestamp (ISO 8601 UTC).</summary>
    public required string Timestamp { get; init; }

    /// <summary>
    /// Ed25519 signature (<c>ed25519:{base64url}</c>) over the canonical JSON of this
    /// frame (minus the <c>signature</c> field), produced with the publisher's private key.
    /// </summary>
    public required string Signature { get; init; }

    // ── NPS-4 v0.7 fields ─────────────────────────────────────────────────────

    /// <summary>
    /// Lifecycle model of this node (NPS-4 §3.1.7):
    /// <c>"ephemeral"</c> — short-lived, evicted quickly;
    /// <c>"resident"</c> — persistent long-running service (default);
    /// <c>"hybrid"</c> — resident base that can spin up ephemeral instances.
    /// Receivers cap effective TTL per activation_mode (§3.1.7.1).
    /// </summary>
    [JsonPropertyName("activation_mode")]
    public string? ActivationMode { get; init; }

    /// <summary>
    /// Semantic roles this node fulfils, e.g. <c>["orchestrator","memory"]</c> (NPS-4 §3.1.8).
    /// Used by consumers to filter nodes by function rather than capability.
    /// </summary>
    [JsonPropertyName("node_roles")]
    public IReadOnlyList<string>? NodeRoles { get; init; }

    /// <summary>
    /// NID of the cluster anchor that coordinates this node's cluster (NPS-4 §3.1.9).
    /// Present only for nodes that are members of a named cluster.
    /// </summary>
    [JsonPropertyName("cluster_anchor")]
    public string? ClusterAnchor { get; init; }

    /// <summary>
    /// URI reference to a machine-readable spawn specification (NPS-4 §3.1.10).
    /// Consumers can fetch this spec to understand how to launch a new instance.
    /// </summary>
    [JsonPropertyName("spawn_spec_ref")]
    public string? SpawnSpecRef { get; init; }

    /// <summary>
    /// Additional bridging protocols supported by this node beyond the primary address
    /// list, e.g. <c>["a2a","mcp"]</c> (NPS-4 §3.1.11).
    /// </summary>
    [JsonPropertyName("bridge_protocols")]
    public IReadOnlyList<string>? BridgeProtocols { get; init; }

    /// <summary>
    /// NWP URL at which the node accepts activation (spawn) requests (NPS-4 §3.1.12).
    /// Present only when <see cref="ActivationMode"/> is <c>"hybrid"</c>.
    /// </summary>
    [JsonPropertyName("activation_endpoint")]
    public string? ActivationEndpoint { get; init; }
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
public sealed record ResolveFrame : IFrame
{
    /// <inheritdoc/>
    [JsonIgnore] public FrameType    FrameType     => FrameType.Resolve;
    /// <inheritdoc/>
    [JsonIgnore] public EncodingTier PreferredTier => EncodingTier.Json;

    /// <summary>Frame type discriminant. Fixed value <c>"0x31"</c>.</summary>
    [JsonPropertyName("frame")]
    public string Frame { get; init; } = "0x31";

    /// <summary>The <c>nwp://</c> URL to resolve, e.g. <c>"nwp://api.example.com/products"</c>.</summary>
    public required string Target { get; init; }

    /// <summary>Requesting party NID for optional authorisation checks. May be null.</summary>
    [JsonPropertyName("requester_nid")]
    public string? RequesterNid { get; init; }

    /// <summary>
    /// Populated in the response; null in a request.
    /// Contains the resolved physical endpoint.
    /// </summary>
    public NdpResolveResult? Resolved { get; init; }
}

// ── GraphFrame (0x32) ─────────────────────────────────────────────────────────

/// <summary>
/// Multi-cluster topology graph snapshot (NPS-4 §5, NDP v0.8).
/// Published by Registries or Anchor Nodes to describe inter-cluster connectivity.
/// Max 256 nodes, max 1024 edges per frame.
/// Oversized frames MUST be rejected with <c>NDP-GRAPH-TOO-LARGE</c>;
/// structural errors with <c>NDP-GRAPH-INVALID</c>.
/// </summary>
public sealed record GraphFrame : IFrame
{
    /// <inheritdoc/>
    [JsonIgnore] public FrameType    FrameType     => FrameType.Graph;
    /// <inheritdoc/>
    [JsonIgnore] public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Frame type discriminant. Fixed value <c>"0x32"</c>.</summary>
    [JsonPropertyName("frame")]
    public string Frame { get; init; } = "0x32";

    /// <summary>Opaque snapshot identifier assigned by the publisher.</summary>
    [JsonPropertyName("graph_id")]
    public required string GraphId { get; init; }

    /// <summary>Node entries in this snapshot. Maximum 256.</summary>
    public required IReadOnlyList<NdpGraphNode> Nodes { get; init; }

    /// <summary>Directed edges describing inter-node connectivity. Maximum 1024.</summary>
    public required IReadOnlyList<NdpGraphEdge> Edges { get; init; }

    /// <summary>Time-to-live for this snapshot in seconds. 0 means no expiry.</summary>
    public required uint Ttl { get; init; }

    /// <summary>Free-form publisher metadata (informational only).</summary>
    public JsonElement? Metadata { get; init; }
}
