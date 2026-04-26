// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NIP;

/// <summary>
/// Agent identity assurance level — one of the three tiers defined by
/// NPS-RFC-0003 / NPS-3 §5.1.1. The level travels in
/// <c>IdentFrame.assurance_level</c> and is the source of truth for Node
/// policy decisions (<c>NWM.min_assurance_level</c>). When X.509 NID
/// certificates are in use (NPS-RFC-0002), the cert MUST also carry the
/// <c>id-nid-assurance-level</c> extension and the two MUST agree.
/// <para>
/// The enum is **ordered**: <see cref="Anonymous"/> &lt;
/// <see cref="Attested"/> &lt; <see cref="Verified"/>. A request whose
/// level is below the Node's required level is rejected with
/// <c>NWP-AUTH-ASSURANCE-TOO-LOW</c> (<c>NPS-AUTH-FORBIDDEN</c>).
/// </para>
/// </summary>
[JsonConverter(typeof(AssuranceLevelJsonConverter))]
public enum AssuranceLevel
{
    /// <summary>
    /// L0 — self-signed NID, or CA-signed without out-of-band identity
    /// binding. Default for any NID lacking an explicit declaration
    /// (backward compatibility with v1.0-alpha.2 publishers).
    /// </summary>
    Anonymous = 0,

    /// <summary>
    /// L1 — NID signed by an RFC-0002-compliant CA; CA attests
    /// possession of the NID private key (e.g. ACME <c>agent-01</c>
    /// challenge); contact email or domain verified. Default for most
    /// production Agents and the typical rate-limit tier.
    /// </summary>
    Attested = 1,

    /// <summary>
    /// L2 — L1 criteria PLUS the CA binds the operator's legal identity
    /// (corporate registration for org-NIDs, signed AaaS-operator
    /// attestation for hosted Agents). Used for regulated integrations,
    /// paid premium tiers, and contract-grade orchestration.
    /// </summary>
    Verified = 2,
}

/// <summary>
/// Wire-string ↔ enum mapping for <see cref="AssuranceLevel"/>. Wire form
/// is the lowercase string (<c>"anonymous"</c>, <c>"attested"</c>,
/// <c>"verified"</c>) per NPS-3 §5.1.1.
/// </summary>
public static class AssuranceLevels
{
    /// <summary>Wire string for <see cref="AssuranceLevel.Anonymous"/>.</summary>
    public const string AnonymousWire = "anonymous";

    /// <summary>Wire string for <see cref="AssuranceLevel.Attested"/>.</summary>
    public const string AttestedWire  = "attested";

    /// <summary>Wire string for <see cref="AssuranceLevel.Verified"/>.</summary>
    public const string VerifiedWire  = "verified";

    /// <summary>
    /// Convert an <see cref="AssuranceLevel"/> to the wire string.
    /// </summary>
    public static string ToWire(AssuranceLevel level) => level switch
    {
        AssuranceLevel.Anonymous => AnonymousWire,
        AssuranceLevel.Attested  => AttestedWire,
        AssuranceLevel.Verified  => VerifiedWire,
        _ => throw new ArgumentOutOfRangeException(
            nameof(level), level,
            "Unknown AssuranceLevel; this would emit NIP-ASSURANCE-UNKNOWN on the wire."),
    };

    /// <summary>
    /// Parse a wire string to an <see cref="AssuranceLevel"/>. Returns
    /// <c>true</c> on a known value; <c>false</c> for any other input
    /// (including <c>null</c> and empty). Receivers that get
    /// <c>false</c> for a non-empty input MUST emit
    /// <c>NIP-ASSURANCE-UNKNOWN</c>.
    /// </summary>
    public static bool TryParse(string? wire, out AssuranceLevel level)
    {
        switch (wire)
        {
            case AnonymousWire: level = AssuranceLevel.Anonymous; return true;
            case AttestedWire:  level = AssuranceLevel.Attested;  return true;
            case VerifiedWire:  level = AssuranceLevel.Verified;  return true;
            default:            level = AssuranceLevel.Anonymous; return false;
        }
    }

    /// <summary>
    /// Per NPS-3 §5.1.1: <c>null</c> on the wire means
    /// <see cref="AssuranceLevel.Anonymous"/> (backward compatibility
    /// with pre-RFC-0003 publishers). Use this instead of
    /// <see cref="TryParse"/> when normalising an optional incoming
    /// field.
    /// </summary>
    public static AssuranceLevel FromWireOrAnonymous(string? wire)
        => TryParse(wire, out var level) ? level : AssuranceLevel.Anonymous;
}

/// <summary>
/// JSON converter that serialises <see cref="AssuranceLevel"/> as the
/// lowercase wire string defined by NPS-3 §5.1.1. Read accepts both
/// the wire string and a missing/null value (treated as
/// <see cref="AssuranceLevel.Anonymous"/>); read of any other string
/// throws <see cref="JsonException"/> and SHOULD be surfaced by the
/// transport layer as <c>NIP-ASSURANCE-UNKNOWN</c>.
/// </summary>
public sealed class AssuranceLevelJsonConverter : JsonConverter<AssuranceLevel>
{
    public override AssuranceLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return AssuranceLevel.Anonymous;

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                $"Expected string for assurance_level, got {reader.TokenType}; map to NIP-ASSURANCE-UNKNOWN.");

        var s = reader.GetString();
        if (AssuranceLevels.TryParse(s, out var level))
            return level;

        throw new JsonException(
            $"Unknown assurance_level value '{s}'; map to NIP-ASSURANCE-UNKNOWN.");
    }

    public override void Write(Utf8JsonWriter writer, AssuranceLevel value, JsonSerializerOptions options)
        => writer.WriteStringValue(AssuranceLevels.ToWire(value));
}
