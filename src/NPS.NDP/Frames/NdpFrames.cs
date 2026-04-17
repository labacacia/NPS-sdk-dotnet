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
/// A single node entry inside a <see cref="GraphFrame"/> full sync (NPS-4 §3.3).
/// </summary>
public sealed record NdpGraphNode
{
    /// <summary>Node NID.</summary>
    public required string Nid { get; init; }

    /// <summary>Node type, e.g. <c>"memory"</c>, <c>"action"</c>, <c>"complex"</c>.</summary>
    [JsonPropertyName("node_type")]
    public string? NodeType { get; init; }

    /// <summary>Physical addresses the node is reachable at.</summary>
    public required IReadOnlyList<NdpAddress> Addresses { get; init; }

    /// <summary>Capabilities the node advertises.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }
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
/// Node graph synchronisation frame (NPS-4 §3.3).
/// Sent by a Registry to subscribers either as a full initial snapshot
/// (<see cref="InitialSync"/> = <c>true</c>) or as incremental JSON Patch updates.
///
/// <para>The <see cref="Seq"/> counter MUST be strictly monotonically increasing per publisher.
/// A gap in sequence numbers SHOULD trigger a re-sync request (error <c>NDP-GRAPH-SEQ-GAP</c>).</para>
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

    /// <summary>
    /// <c>true</c> = full snapshot; <see cref="Nodes"/> is populated.
    /// <c>false</c> = incremental; <see cref="Patch"/> (RFC 6902 JSON Patch) is populated.
    /// </summary>
    [JsonPropertyName("initial_sync")]
    public required bool InitialSync { get; init; }

    /// <summary>Full node list when <see cref="InitialSync"/> is <c>true</c>. Null otherwise.</summary>
    public IReadOnlyList<NdpGraphNode>? Nodes { get; init; }

    /// <summary>
    /// RFC 6902 JSON Patch array when <see cref="InitialSync"/> is <c>false</c>. Null otherwise.
    /// </summary>
    public JsonElement? Patch { get; init; }

    /// <summary>Monotonically increasing graph version sequence number.</summary>
    public required ulong Seq { get; init; }
}
