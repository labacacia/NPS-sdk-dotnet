// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NIP.Reputation;

/// <summary>
/// A single signed observation about a <see cref="SubjectNid"/> made
/// by an <see cref="IssuerNid"/> and committed to an append-only log
/// operated by <see cref="LogId"/>. Wire format defined by NPS-3 §5.1.2
/// / NPS-RFC-0004 §4.1.
/// <para>
/// Phase 1 of NPS-RFC-0004 ships this record + signing helpers
/// (<see cref="ReputationLogEntrySigner"/>) only; Merkle tree, STH,
/// inclusion-proof, and the operator HTTP API land in Phase 2 alongside
/// the NDP <c>/.nid/reputation</c> discovery surface.
/// </para>
/// </summary>
public sealed record ReputationLogEntry
{
    /// <summary>Schema version. NPS-RFC-0004 defines <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public required int Version { get; init; }

    /// <summary>NID of the log operator that appended (or will append) this entry.</summary>
    [JsonPropertyName("log_id")]
    public required string LogId { get; init; }

    /// <summary>Monotonically-increasing per-<see cref="LogId"/> sequence number.</summary>
    [JsonPropertyName("seq")]
    public required ulong Seq { get; init; }

    /// <summary>Log operator's commit time. RFC 3339 UTC.</summary>
    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    /// <summary>The NID this entry is about.</summary>
    [JsonPropertyName("subject_nid")]
    public required string SubjectNid { get; init; }

    /// <summary>
    /// Strongly-typed incident category. <see cref="IncidentType.Other"/>
    /// indicates a forward-compatible unknown value — read the original
    /// wire string from <see cref="IncidentRaw"/>.
    /// </summary>
    [JsonPropertyName("incident")]
    public required IncidentType Incident { get; init; }

    /// <summary>
    /// The original on-the-wire <c>incident</c> string, captured for
    /// forward-compatibility round-tripping. Set by deserialiser when
    /// the value is unknown; leave <c>null</c> when constructing
    /// known-incident entries (the serialiser will emit the wire form
    /// from <see cref="Incident"/>).
    /// </summary>
    [JsonIgnore]
    public string? IncidentRaw { get; init; }

    /// <summary>Severity ladder — see <see cref="Reputation.Severity"/>.</summary>
    [JsonPropertyName("severity")]
    public required Severity Severity { get; init; }

    /// <summary>
    /// Optional time window the observation covers (e.g. when the
    /// <see cref="IncidentType.RateLimitViolation"/> was sustained).
    /// </summary>
    [JsonPropertyName("window")]
    public ObservationWindow? Window { get; init; }

    /// <summary>
    /// Free-form, machine-readable, per-incident-type detail
    /// (e.g. <c>{requests: 45000, threshold: 300}</c>). Stored as a
    /// <see cref="JsonElement"/> so each incident's adapter can parse
    /// it without forcing this record to know every shape.
    /// </summary>
    [JsonPropertyName("observation")]
    public JsonElement? Observation { get; init; }

    /// <summary>URL where richer evidence (logs, transcripts) lives.</summary>
    [JsonPropertyName("evidence_ref")]
    public string? EvidenceRef { get; init; }

    /// <summary>SHA-256 of the evidence blob (lowercase hex, no prefix).</summary>
    [JsonPropertyName("evidence_sha256")]
    public string? EvidenceSha256 { get; init; }

    /// <summary>
    /// NID of the party making the assertion. MAY equal
    /// <see cref="LogId"/> for self-published entries.
    /// </summary>
    [JsonPropertyName("issuer_nid")]
    public required string IssuerNid { get; init; }

    /// <summary>
    /// <c>{alg}:{base64url}</c> Ed25519 signature by
    /// <see cref="IssuerNid"/>'s private key over the entry minus the
    /// <c>signature</c> field, canonicalised per RFC 8785 (JCS).
    /// Use <see cref="ReputationLogEntrySigner.Sign"/> to produce.
    /// </summary>
    [JsonPropertyName("signature")]
    public required string Signature { get; init; }
}

/// <summary>
/// <c>{ start, end }</c> pair on <see cref="ReputationLogEntry.Window"/>.
/// Both endpoints are RFC 3339 UTC strings.
/// </summary>
public sealed record ObservationWindow(
    [property: JsonPropertyName("start")] string Start,
    [property: JsonPropertyName("end")]   string End);
