// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Frames;
using NPS.NIP;

namespace NPS.NIP.Frames;

// ── IdentFrame (0x20) ────────────────────────────────────────────────────────

/// <summary>
/// Agent identity declaration and certificate carrier (NPS-3 §5.1).
/// Sent as a handshake frame when establishing a connection; Nodes verify it
/// before granting access.
/// </summary>
public sealed record IdentFrame : IFrame
{
    /// <inheritdoc/>
    [JsonIgnore] public FrameType    FrameType     => FrameType.Ident;
    /// <inheritdoc/>
    [JsonIgnore] public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Frame type discriminant. Fixed value <c>"0x20"</c>.</summary>
    [JsonPropertyName("frame")]
    public string Frame { get; init; } = "0x20";

    /// <summary>Agent NID, e.g. <c>urn:nps:agent:ca.example.com:&lt;uuid&gt;</c>.</summary>
    public required string Nid { get; init; }

    /// <summary>Public key in <c>{alg}:{base64url(DER)}</c> format, e.g. <c>ed25519:MCow...</c>.</summary>
    [JsonPropertyName("pub_key")]
    public required string PubKey { get; init; }

    /// <summary>Capability list, e.g. <c>["nwp:query", "nwp:stream"]</c>.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    /// Access scope declaration. Contains <c>nodes</c>, <c>actions</c>,
    /// and optionally <c>max_token_budget</c>.
    /// </summary>
    public required JsonElement Scope { get; init; }

    /// <summary>Issuer NID (Org CA), e.g. <c>urn:nps:org:ca.example.com</c>.</summary>
    [JsonPropertyName("issued_by")]
    public required string IssuedBy { get; init; }

    /// <summary>Issuance timestamp (ISO 8601 UTC).</summary>
    [JsonPropertyName("issued_at")]
    public required string IssuedAt { get; init; }

    /// <summary>Expiry timestamp (ISO 8601 UTC).</summary>
    [JsonPropertyName("expires_at")]
    public required string ExpiresAt { get; init; }

    /// <summary>Globally unique certificate serial number (hex string, e.g. <c>0x0A3F9C</c>).</summary>
    public required string Serial { get; init; }

    /// <summary>
    /// CA signature over the canonical JSON of this frame (minus the <c>signature</c> field),
    /// in <c>{alg}:{base64url}</c> format.
    /// </summary>
    public required string Signature { get; init; }

    /// <summary>Optional agent metadata (model_family, tokenizer, runtime). Not signed.</summary>
    [JsonPropertyName("metadata")]
    public IdentMetadata? Metadata { get; init; }

    /// <summary>
    /// Agent identity assurance level (NPS-RFC-0003 / NPS-3 §5.1.1).
    /// Optional on the wire — pre-RFC-0003 publishers omit it. Receivers
    /// MUST treat an absent field as <see cref="AssuranceLevel.Anonymous"/>
    /// (backward compatibility with v1.0-alpha.2 publishers). When the
    /// NID's certificate carries the <c>id-nid-assurance-level</c>
    /// extension, this field MUST equal it; mismatch is
    /// <c>NIP-ASSURANCE-MISMATCH</c>.
    /// </summary>
    [JsonPropertyName("assurance_level")]
    public AssuranceLevel? AssuranceLevel { get; init; }

    /// <summary>
    /// Cert format discriminator for NPS-RFC-0002 §4.5.
    /// <list type="bullet">
    /// <item><c>null</c> or <c>"v1-proprietary"</c> — the legacy CA-signed JSON
    /// IdentFrame (this record's other fields). v1.0-alpha.3 default.</item>
    /// <item><c>"v2-x509"</c> — additionally carries a DER X.509 cert chain in
    /// <see cref="CertChain"/>. The legacy <see cref="Signature"/> field is
    /// still populated (CA Ed25519 over canonical JSON) so v1 verifiers
    /// remain functional; v2 verifiers ALSO validate the X.509 chain
    /// (EKU, SAN URI, subject CN, issuer signature).</item>
    /// </list>
    /// Wire-level dual mode keeps the migration non-breaking during Phase 1
    /// (RFC §8.1) while letting both verifier paths exercise the same frame.
    /// </summary>
    [JsonPropertyName("cert_format")]
    public string? CertFormat { get; init; }

    /// <summary>
    /// X.509 certificate chain (NPS-RFC-0002 §4.1), populated only when
    /// <see cref="CertFormat"/> is <c>"v2-x509"</c>. Each element is a
    /// base64url-encoded DER certificate. Element <c>[0]</c> is the leaf
    /// (containing the agent's public key + NPS EKU + SAN URI matching
    /// <see cref="Nid"/>); subsequent elements are intermediates up to but
    /// not including the trusted root.
    /// </summary>
    [JsonPropertyName("cert_chain")]
    public IReadOnlyList<string>? CertChain { get; init; }
}

/// <summary>String constants for <see cref="IdentFrame.CertFormat"/>
/// (NPS-RFC-0002 §4.5).</summary>
public static class IdentCertFormat
{
    /// <summary>Legacy CA-signed JSON cert (default before NPS-RFC-0002).</summary>
    public const string V1Proprietary = "v1-proprietary";

    /// <summary>X.509 DER cert chain in <see cref="IdentFrame.CertChain"/>.</summary>
    public const string V2X509        = "v2-x509";
}

/// <summary>
/// Optional metadata carried in an <see cref="IdentFrame"/> (NPS-3 §5.1).
/// Not included in signature computation — Agents may set dynamically at runtime.
/// </summary>
public sealed record IdentMetadata
{
    /// <summary>Model family identifier, e.g. <c>"anthropic/claude-4"</c>.</summary>
    [JsonPropertyName("model_family")]
    public string? ModelFamily { get; init; }

    /// <summary>Tokenizer identifier, e.g. <c>"cl100k_base"</c>.</summary>
    public string? Tokenizer { get; init; }

    /// <summary>Runtime identifier, e.g. <c>"langchain/0.2"</c>.</summary>
    public string? Runtime { get; init; }
}

// ── TrustFrame (0x21) ────────────────────────────────────────────────────────

/// <summary>
/// Cross-CA trust chain and capability grant frame (NPS-3 §5.2).
/// <para>
/// ⚠️ Business logic for trust chain validation is a commercial NPS Cloud feature.
/// This record provides the frame definition for codec use; trust chain enforcement
/// is not implemented in the OSS library.
/// </para>
/// </summary>
public sealed record TrustFrame : IFrame
{
    /// <inheritdoc/>
    [JsonIgnore] public FrameType    FrameType     => FrameType.Trust;
    /// <inheritdoc/>
    [JsonIgnore] public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Frame type discriminant. Fixed value <c>"0x21"</c>.</summary>
    [JsonPropertyName("frame")]
    public string Frame { get; init; } = "0x21";

    /// <summary>NID of the granting organisation CA.</summary>
    [JsonPropertyName("grantor_nid")]
    public required string GrantorNid { get; init; }

    /// <summary>NID of the CA being granted trust.</summary>
    [JsonPropertyName("grantee_ca")]
    public required string GranteeCa { get; init; }

    /// <summary>Capability scope granted to the grantee, e.g. <c>["nwp:query"]</c>.</summary>
    [JsonPropertyName("trust_scope")]
    public required IReadOnlyList<string> TrustScope { get; init; }

    /// <summary>Node paths the grant covers, e.g. <c>["nwp://api.org-a.com/public/*"]</c>.</summary>
    public required IReadOnlyList<string> Nodes { get; init; }

    /// <summary>Grant expiry (ISO 8601 UTC).</summary>
    [JsonPropertyName("expires_at")]
    public required string ExpiresAt { get; init; }

    /// <summary>Grantor CA signature (<c>ed25519:{base64url}</c>).</summary>
    public required string Signature { get; init; }
}

// ── RevokeFrame (0x22) ───────────────────────────────────────────────────────

/// <summary>
/// Certificate revocation frame (NPS-3 §5.3).
/// Issued by the CA or an Operator to immediately invalidate a NID.
/// </summary>
public sealed record RevokeFrame : IFrame
{
    /// <inheritdoc/>
    [JsonIgnore] public FrameType    FrameType     => FrameType.Revoke;
    /// <inheritdoc/>
    [JsonIgnore] public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Frame type discriminant. Fixed value <c>"0x22"</c>.</summary>
    [JsonPropertyName("frame")]
    public string Frame { get; init; } = "0x22";

    /// <summary>NID of the certificate being revoked.</summary>
    [JsonPropertyName("target_nid")]
    public required string TargetNid { get; init; }

    /// <summary>Serial number of the certificate being revoked.</summary>
    public required string Serial { get; init; }

    /// <summary>
    /// Revocation reason. One of: <c>key_compromise</c>, <c>ca_compromise</c>,
    /// <c>affiliation_changed</c>, <c>superseded</c>, <c>cessation_of_operation</c>.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>Revocation timestamp (ISO 8601 UTC).</summary>
    [JsonPropertyName("revoked_at")]
    public required string RevokedAt { get; init; }

    /// <summary>CA signature over the canonical JSON of this frame (minus <c>signature</c>).</summary>
    public required string Signature { get; init; }
}
