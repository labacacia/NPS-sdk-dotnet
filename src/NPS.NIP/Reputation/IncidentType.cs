// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NIP.Reputation;

/// <summary>
/// Initial reputation-log incident vocabulary, per NPS-3 §5.1.2.1 /
/// NPS-RFC-0004 §4.2. Receivers MUST preserve and round-trip
/// <see cref="Other"/> values they do not recognise so the wire is
/// forward-compatible with future-CR-registered incident types.
/// </summary>
[JsonConverter(typeof(IncidentTypeJsonConverter))]
public enum IncidentType
{
    /// <summary>
    /// Sentinel representing an unknown wire value the receiver chose
    /// to keep as opaque pass-through. Use the
    /// <see cref="ReputationLogEntry.IncidentRaw"/> field to read the
    /// original string.
    /// </summary>
    Other = 0,

    /// <summary>CA revoked the <c>subject_nid</c>'s certificate.</summary>
    CertRevoked,

    /// <summary>Sustained violation of published rate limits.</summary>
    RateLimitViolation,

    /// <summary>Violated an AaaS Anchor Node's published terms of service.</summary>
    TosViolation,

    /// <summary>Behavior matched scraping-pattern heuristics.</summary>
    ScrapingPattern,

    /// <summary>NPT or fiat default on a committed transaction.</summary>
    PaymentDefault,

    /// <summary>Unresolved contractual breach on an asynchronous NOP task.</summary>
    ContractDispute,

    /// <summary>Third-party claim that <c>subject_nid</c> is impersonating them.</summary>
    ImpersonationClaim,

    /// <summary>Explicit positive signal — e.g. an audit passed.</summary>
    PositiveAttestation,
}

/// <summary>
/// Wire-string ↔ enum mapping for <see cref="IncidentType"/>. Wire form
/// is the kebab-case ASCII string defined by NPS-3 §5.1.2.1.
/// </summary>
public static class IncidentTypes
{
    /// <summary>Wire string for <see cref="IncidentType.CertRevoked"/>.</summary>
    public const string CertRevokedWire        = "cert-revoked";

    /// <summary>Wire string for <see cref="IncidentType.RateLimitViolation"/>.</summary>
    public const string RateLimitViolationWire = "rate-limit-violation";

    /// <summary>Wire string for <see cref="IncidentType.TosViolation"/>.</summary>
    public const string TosViolationWire       = "tos-violation";

    /// <summary>Wire string for <see cref="IncidentType.ScrapingPattern"/>.</summary>
    public const string ScrapingPatternWire    = "scraping-pattern";

    /// <summary>Wire string for <see cref="IncidentType.PaymentDefault"/>.</summary>
    public const string PaymentDefaultWire     = "payment-default";

    /// <summary>Wire string for <see cref="IncidentType.ContractDispute"/>.</summary>
    public const string ContractDisputeWire    = "contract-dispute";

    /// <summary>Wire string for <see cref="IncidentType.ImpersonationClaim"/>.</summary>
    public const string ImpersonationClaimWire = "impersonation-claim";

    /// <summary>Wire string for <see cref="IncidentType.PositiveAttestation"/>.</summary>
    public const string PositiveAttestationWire = "positive-attestation";

    /// <summary>Convert a known <see cref="IncidentType"/> to its wire string.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is <see cref="IncidentType.Other"/> or an
    /// undefined value. Use <see cref="ReputationLogEntry.IncidentRaw"/>
    /// for opaque pass-through instead.
    /// </exception>
    public static string ToWire(IncidentType kind) => kind switch
    {
        IncidentType.CertRevoked         => CertRevokedWire,
        IncidentType.RateLimitViolation  => RateLimitViolationWire,
        IncidentType.TosViolation        => TosViolationWire,
        IncidentType.ScrapingPattern     => ScrapingPatternWire,
        IncidentType.PaymentDefault      => PaymentDefaultWire,
        IncidentType.ContractDispute     => ContractDisputeWire,
        IncidentType.ImpersonationClaim  => ImpersonationClaimWire,
        IncidentType.PositiveAttestation => PositiveAttestationWire,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind,
            "IncidentType.Other (or any undefined value) cannot be serialised — preserve the original wire string via ReputationLogEntry.IncidentRaw."),
    };

    /// <summary>
    /// Parse a wire string to an <see cref="IncidentType"/>. Returns
    /// <c>true</c> for a known value; <c>false</c> for any unknown
    /// (including null / empty). Per §5.1.2.1, callers that get
    /// <c>false</c> on a non-empty input MUST keep the original string
    /// for forward compatibility.
    /// </summary>
    public static bool TryParse(string? wire, out IncidentType kind)
    {
        switch (wire)
        {
            case CertRevokedWire:        kind = IncidentType.CertRevoked;         return true;
            case RateLimitViolationWire: kind = IncidentType.RateLimitViolation;  return true;
            case TosViolationWire:       kind = IncidentType.TosViolation;        return true;
            case ScrapingPatternWire:    kind = IncidentType.ScrapingPattern;     return true;
            case PaymentDefaultWire:     kind = IncidentType.PaymentDefault;      return true;
            case ContractDisputeWire:    kind = IncidentType.ContractDispute;     return true;
            case ImpersonationClaimWire: kind = IncidentType.ImpersonationClaim;  return true;
            case PositiveAttestationWire: kind = IncidentType.PositiveAttestation; return true;
            default:                     kind = IncidentType.Other;               return false;
        }
    }
}

/// <summary>
/// JSON converter mapping <see cref="IncidentType"/> to / from the
/// kebab-case wire string. Reads of unknown strings yield
/// <see cref="IncidentType.Other"/> (the raw string is recoverable
/// via <see cref="ReputationLogEntry.IncidentRaw"/>); reads of any
/// non-string token throw.
/// </summary>
public sealed class IncidentTypeJsonConverter : JsonConverter<IncidentType>
{
    public override IncidentType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                $"Expected string for incident, got {reader.TokenType}; map to NIP-REPUTATION-ENTRY-INVALID.");

        var s = reader.GetString();
        return IncidentTypes.TryParse(s, out var kind) ? kind : IncidentType.Other;
    }

    public override void Write(Utf8JsonWriter writer, IncidentType value, JsonSerializerOptions options)
    {
        if (value == IncidentType.Other)
            throw new JsonException(
                "IncidentType.Other cannot be written directly. Construct the entry via ReputationLogEntry.IncidentRaw to preserve the original wire string.");
        writer.WriteStringValue(IncidentTypes.ToWire(value));
    }
}
