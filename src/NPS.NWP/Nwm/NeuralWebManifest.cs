// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace NPS.NWP.Nwm;

/// <summary>
/// Neural Web Manifest (NWM) — the machine-readable capability descriptor exposed
/// at <c>/.nwm</c> on every NWP node (NPS-2 §4).
/// MIME type: <c>application/nwp-manifest+json</c>.
/// </summary>
public sealed record NeuralWebManifest
{
    /// <summary>NWP version. Current value: <c>"0.2"</c>.</summary>
    public required string Nwp { get; init; }

    /// <summary>
    /// Node NID in <c>urn:nps:node:{host}:{path}</c> format,
    /// e.g. <c>"urn:nps:node:api.example.com:products"</c>.
    /// </summary>
    [JsonPropertyName("node_id")]
    public required string NodeId { get; init; }

    /// <summary>Node type: <c>"memory"</c>, <c>"action"</c>, or <c>"complex"</c>.</summary>
    [JsonPropertyName("node_type")]
    public required string NodeType { get; init; }

    /// <summary>Human-readable node name shown in developer tooling.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    /// <summary>
    /// Supported wire encodings, e.g. <c>["ncp-capsule", "msgpack", "json"]</c>.
    /// </summary>
    [JsonPropertyName("wire_formats")]
    public required IReadOnlyList<string> WireFormats { get; init; }

    /// <summary>Preferred encoding format for responses.</summary>
    [JsonPropertyName("preferred_format")]
    public required string PreferredFormat { get; init; }

    /// <summary>
    /// Pre-declared schema anchors as <c>{ name → anchor_id }</c>.
    /// Agents can warm up their cache before the first query.
    /// </summary>
    [JsonPropertyName("schema_anchors")]
    public IReadOnlyDictionary<string, string>? SchemaAnchors { get; init; }

    /// <summary>Capability flags for this node (NPS-2 §4.2).</summary>
    public required NodeCapabilities Capabilities { get; init; }

    /// <summary>Identifiers of the underlying data sources, e.g. <c>"rds:products_db"</c>.</summary>
    [JsonPropertyName("data_sources")]
    public IReadOnlyList<string>? DataSources { get; init; }

    /// <summary>Authentication requirements for this node (NPS-2 §4.3).</summary>
    public required NodeAuth Auth { get; init; }

    /// <summary>Functional endpoint URLs (NPS-2 §4.1).</summary>
    public required NodeEndpoints Endpoints { get; init; }

    /// <summary>
    /// Tokenizer identifiers this node supports for CGN conversion
    /// (e.g. <c>["cl100k_base", "claude"]</c>). See token-budget.md.
    /// </summary>
    [JsonPropertyName("tokenizer_support")]
    public IReadOnlyList<string>? TokenizerSupport { get; init; }

    /// <summary>Sub-node graph declaration. Only present on Complex Nodes (NPS-2 §9).</summary>
    public NodeGraph? Graph { get; init; }

    /// <summary>
    /// Minimum required Agent identity assurance level (NPS-2 §4.1 /
    /// NPS-3 §5.1.1 / NPS-RFC-0003). One of <c>"anonymous"</c>
    /// (default), <c>"attested"</c>, <c>"verified"</c>. Requests whose
    /// presented level is lower MUST be rejected with
    /// <c>NWP-AUTH-ASSURANCE-TOO-LOW</c> (<c>NPS-AUTH-FORBIDDEN</c>).
    /// Per-action overrides are permitted via <c>auth.min_assurance_level</c>
    /// on individual <c>ActionSpec</c>s (§4.6).
    /// <para>
    /// In Phase 1 of NPS-RFC-0003 the reference NWM model parses and
    /// round-trips this field but enforcement is opt-in — Nodes that
    /// declare a stricter level MUST wire enforcement through their
    /// own request pipeline until Phase 2 ships.
    /// </para>
    /// </summary>
    [JsonPropertyName("min_assurance_level")]
    public string? MinAssuranceLevel { get; init; }
}

/// <summary>
/// Capability flags advertised in the NWM (NPS-2 §4.2).
/// </summary>
public sealed record NodeCapabilities
{
    /// <summary>Supports <c>QueryFrame (0x10)</c>.</summary>
    public bool Query { get; init; }

    /// <summary>Supports <c>StreamFrame (0x03)</c> responses for large result sets.</summary>
    public bool Stream { get; init; }

    /// <summary>Supports change subscriptions via <c>DiffFrame (0x02)</c> push.</summary>
    public bool Subscribe { get; init; }

    /// <summary>Supports vector similarity search (<c>QueryFrame.vector_search</c>).</summary>
    [JsonPropertyName("vector_search")]
    public bool VectorSearch { get; init; }

    /// <summary>Supports response trimming based on the <c>X-NWP-Budget</c> header.</summary>
    [JsonPropertyName("token_budget_hint")]
    public bool TokenBudgetHint { get; init; }

    /// <summary>Supports extended frame header (8-byte, EXT=1) for large payloads (NPS-1 §3.1).</summary>
    [JsonPropertyName("ext_frame")]
    public bool ExtFrame { get; init; }
}

/// <summary>
/// Authentication requirements declared in the NWM (NPS-2 §4.3).
/// </summary>
public sealed record NodeAuth
{
    /// <summary>Whether authentication is required for all requests.</summary>
    public required bool Required { get; init; }

    /// <summary>
    /// Identity scheme: <c>"nip-cert"</c>, <c>"bearer"</c>, or <c>"none"</c>.
    /// Required when <see cref="Required"/> is <c>true</c>.
    /// </summary>
    [JsonPropertyName("identity_type")]
    public string? IdentityType { get; init; }

    /// <summary>
    /// URLs of CA servers whose NID certificates are trusted.
    /// Required when <see cref="IdentityType"/> is <c>"nip-cert"</c>.
    /// </summary>
    [JsonPropertyName("trusted_issuers")]
    public IReadOnlyList<string>? TrustedIssuers { get; init; }

    /// <summary>
    /// Capabilities the Agent NID MUST declare, e.g. <c>["nwp:query"]</c>.
    /// </summary>
    [JsonPropertyName("required_capabilities")]
    public IReadOnlyList<string>? RequiredCapabilities { get; init; }

    /// <summary>Scope-matching mode: <c>"prefix"</c> (default) or <c>"exact"</c>.</summary>
    [JsonPropertyName("scope_check")]
    public string? ScopeCheck { get; init; }

    /// <summary>OCSP responder URL for certificate revocation checks.</summary>
    [JsonPropertyName("ocsp_url")]
    public string? OcspUrl { get; init; }
}

/// <summary>
/// Functional endpoint URLs advertised in the NWM (NPS-2 §4.1).
/// </summary>
public sealed record NodeEndpoints
{
    /// <summary>Structured query endpoint (<c>nwp://.../query</c>). Memory / Complex nodes.</summary>
    public string? Query { get; init; }

    /// <summary>Streaming query endpoint (<c>nwp://.../stream</c>). Memory / Complex nodes.</summary>
    public string? Stream { get; init; }

    /// <summary>Action invocation endpoint (<c>nwp://.../invoke</c>). Action / Complex nodes.</summary>
    public string? Invoke { get; init; }

    /// <summary>Schema endpoint (<c>nwp://.../.schema</c>). All nodes.</summary>
    public string? Schema { get; init; }
}

/// <summary>
/// Sub-node graph declaration for Complex Nodes (NPS-2 §9).
/// </summary>
public sealed record NodeGraph
{
    /// <summary>References to child nodes that can be traversed via <c>X-NWP-Depth</c>.</summary>
    public required IReadOnlyList<NodeGraphRef> Refs { get; init; }

    /// <summary>Maximum traversal depth this node will honour. Absolute cap: 5 (NPS-2 §7.1).</summary>
    [JsonPropertyName("max_depth")]
    public uint MaxDepth { get; init; } = 2;
}

/// <summary>
/// A single child-node reference within a <see cref="NodeGraph"/>.
/// </summary>
/// <param name="Rel">Semantic relationship label, e.g. <c>"user"</c>, <c>"payment"</c>.</param>
/// <param name="Node">NWP address of the child node.</param>
public sealed record NodeGraphRef(
    [property: JsonPropertyName("rel")]  string Rel,
    [property: JsonPropertyName("node")] string Node);
