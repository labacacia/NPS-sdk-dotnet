// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace NPS.NIP.Acme;

// ── Wire constants ───────────────────────────────────────────────────────────

/// <summary>String constants used on the ACME wire (RFC 8555 + RFC-0002 §4.4).</summary>
public static class AcmeWire
{
    /// <summary>Content-Type for ACME POST requests (RFC 8555 §6.2).</summary>
    public const string ContentTypeJoseJson = "application/jose+json";

    /// <summary>Content-Type for problem-detail error responses (RFC 7807).</summary>
    public const string ContentTypeProblem  = "application/problem+json";

    /// <summary>Content-Type for issued certificates (RFC 8555 §7.4.2).</summary>
    public const string ContentTypePemCert  = "application/pem-certificate-chain";

    /// <summary>NPS-RFC-0002 §4.4 — new ACME challenge type for NID identity proof.</summary>
    public const string ChallengeAgent01    = "agent-01";

    /// <summary>RFC-0002 §4.4 — identifier type for an NID-targeted ACME order.</summary>
    public const string IdentifierTypeNid   = "nid";
}

// ── Directory ────────────────────────────────────────────────────────────────

/// <summary>RFC 8555 §7.1.1 directory document.</summary>
public sealed record AcmeDirectory
{
    [JsonPropertyName("newNonce")]   public required string NewNonce   { get; init; }
    [JsonPropertyName("newAccount")] public required string NewAccount { get; init; }
    [JsonPropertyName("newOrder")]   public required string NewOrder   { get; init; }

    [JsonPropertyName("revokeCert")] public string? RevokeCert { get; init; }
    [JsonPropertyName("keyChange")]  public string? KeyChange  { get; init; }

    [JsonPropertyName("meta")]       public AcmeDirectoryMeta? Meta { get; init; }
}

public sealed record AcmeDirectoryMeta
{
    [JsonPropertyName("termsOfService")]   public string? TermsOfService   { get; init; }
    [JsonPropertyName("website")]          public string? Website          { get; init; }
    [JsonPropertyName("caaIdentities")]    public IReadOnlyList<string>? CaaIdentities { get; init; }
    [JsonPropertyName("externalAccountRequired")] public bool? ExternalAccountRequired { get; init; }
}

// ── Account ──────────────────────────────────────────────────────────────────

/// <summary>RFC 8555 §7.3 newAccount payload (subset).</summary>
public sealed record AcmeNewAccountPayload
{
    [JsonPropertyName("termsOfServiceAgreed")] public bool TermsOfServiceAgreed { get; init; }

    [JsonPropertyName("contact")] public IReadOnlyList<string>? Contact { get; init; }

    [JsonPropertyName("onlyReturnExisting")] public bool? OnlyReturnExisting { get; init; }
}

/// <summary>RFC 8555 §7.1.2 account resource.</summary>
public sealed record AcmeAccount
{
    [JsonPropertyName("status")]  public required string Status { get; init; }   // valid / deactivated / revoked
    [JsonPropertyName("contact")] public IReadOnlyList<string>? Contact { get; init; }
    [JsonPropertyName("orders")]  public string? Orders { get; init; }
}

// ── Order ────────────────────────────────────────────────────────────────────

/// <summary>RFC 8555 §7.1.3 order resource.</summary>
public sealed record AcmeOrder
{
    [JsonPropertyName("status")]      public required string Status { get; init; }    // pending / ready / processing / valid / invalid
    [JsonPropertyName("expires")]     public string? Expires { get; init; }
    [JsonPropertyName("identifiers")] public required IReadOnlyList<AcmeIdentifier> Identifiers { get; init; }
    [JsonPropertyName("authorizations")] public required IReadOnlyList<string> Authorizations { get; init; }
    [JsonPropertyName("finalize")]    public required string Finalize { get; init; }
    [JsonPropertyName("certificate")] public string? Certificate { get; init; }
    [JsonPropertyName("error")]       public AcmeProblemDetail? Error { get; init; }
}

/// <summary>RFC 8555 §7.1.4 — identifier for an order. Type extended with <c>nid</c> per RFC-0002 §4.4.</summary>
public sealed record AcmeIdentifier(
    [property: JsonPropertyName("type")]  string Type,
    [property: JsonPropertyName("value")] string Value);

// ── Authorization + Challenge ────────────────────────────────────────────────

/// <summary>RFC 8555 §7.1.4 authorization resource.</summary>
public sealed record AcmeAuthorization
{
    [JsonPropertyName("status")]     public required string Status { get; init; }      // pending / valid / invalid / ...
    [JsonPropertyName("expires")]    public string? Expires { get; init; }
    [JsonPropertyName("identifier")] public required AcmeIdentifier Identifier { get; init; }
    [JsonPropertyName("challenges")] public required IReadOnlyList<AcmeChallenge> Challenges { get; init; }
}

/// <summary>
/// RFC 8555 §7.1.5 challenge resource. The <see cref="Type"/> field is the
/// hook for new challenge types — <c>agent-01</c> per NPS-RFC-0002 §4.4.
/// </summary>
public sealed record AcmeChallenge
{
    [JsonPropertyName("type")]      public required string Type   { get; init; }
    [JsonPropertyName("url")]       public required string Url    { get; init; }
    [JsonPropertyName("status")]    public required string Status { get; init; }   // pending / processing / valid / invalid
    [JsonPropertyName("token")]     public required string Token  { get; init; }
    [JsonPropertyName("validated")] public string? Validated { get; init; }
    [JsonPropertyName("error")]     public AcmeProblemDetail? Error { get; init; }
}

/// <summary>
/// Body sent by the client to <see cref="AcmeChallenge.Url"/> to indicate
/// readiness for verification (RFC 8555 §7.5.1). The <c>signature</c> field
/// is RFC-0002 §4.4-specific — Ed25519 over the challenge token, proving
/// possession of the NID private key.
/// </summary>
public sealed record AcmeChallengeRespondPayload
{
    /// <summary>For <c>agent-01</c>: base64url Ed25519 signature over the challenge token.</summary>
    [JsonPropertyName("agent_signature")]
    public string? AgentSignature { get; init; }
}

// ── Finalize / Cert ──────────────────────────────────────────────────────────

/// <summary>RFC 8555 §7.4 finalize payload.</summary>
public sealed record AcmeFinalizePayload(
    [property: JsonPropertyName("csr")] string Csr);

// ── Errors ───────────────────────────────────────────────────────────────────

/// <summary>RFC 8555 §6.7 problem-detail error body (RFC 7807 +ACME).</summary>
public sealed record AcmeProblemDetail
{
    [JsonPropertyName("type")]   public required string Type { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("status")] public int? Status { get; init; }
}
