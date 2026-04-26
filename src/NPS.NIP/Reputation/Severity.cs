// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NIP.Reputation;

/// <summary>
/// Reputation-log incident severity, per NPS-3 §5.1.2 / NPS-RFC-0004 §4.1.
/// Defined as an **ordered** enum so policy rules like
/// <c>severity: ">=major"</c> can be evaluated by simple integer
/// comparison.
/// </summary>
[JsonConverter(typeof(SeverityJsonConverter))]
public enum Severity
{
    /// <summary>Informational only — no action expected.</summary>
    Info     = 0,

    /// <summary>Minor anomaly — note for trend analysis.</summary>
    Minor    = 1,

    /// <summary>Moderate concern — review recommended.</summary>
    Moderate = 2,

    /// <summary>Major incident — automatic mitigation likely warranted.</summary>
    Major    = 3,

    /// <summary>Critical incident — immediate enforcement appropriate.</summary>
    Critical = 4,
}

/// <summary>
/// Wire-string ↔ enum mapping for <see cref="Severity"/>. Wire form
/// is the lowercase ASCII string defined by NPS-3 §5.1.2.
/// </summary>
public static class Severities
{
    /// <summary>Wire string for <see cref="Severity.Info"/>.</summary>
    public const string InfoWire     = "info";

    /// <summary>Wire string for <see cref="Severity.Minor"/>.</summary>
    public const string MinorWire    = "minor";

    /// <summary>Wire string for <see cref="Severity.Moderate"/>.</summary>
    public const string ModerateWire = "moderate";

    /// <summary>Wire string for <see cref="Severity.Major"/>.</summary>
    public const string MajorWire    = "major";

    /// <summary>Wire string for <see cref="Severity.Critical"/>.</summary>
    public const string CriticalWire = "critical";

    /// <summary>Convert <see cref="Severity"/> to its wire string.</summary>
    public static string ToWire(Severity sev) => sev switch
    {
        Severity.Info     => InfoWire,
        Severity.Minor    => MinorWire,
        Severity.Moderate => ModerateWire,
        Severity.Major    => MajorWire,
        Severity.Critical => CriticalWire,
        _ => throw new ArgumentOutOfRangeException(nameof(sev), sev, "Unknown Severity."),
    };

    /// <summary>Parse a wire string. Returns <c>true</c> on a known value.</summary>
    public static bool TryParse(string? wire, out Severity sev)
    {
        switch (wire)
        {
            case InfoWire:     sev = Severity.Info;     return true;
            case MinorWire:    sev = Severity.Minor;    return true;
            case ModerateWire: sev = Severity.Moderate; return true;
            case MajorWire:    sev = Severity.Major;    return true;
            case CriticalWire: sev = Severity.Critical; return true;
            default:           sev = Severity.Info;     return false;
        }
    }
}

/// <summary>
/// JSON converter mapping <see cref="Severity"/> to / from the
/// lowercase wire string defined by NPS-3 §5.1.2. Unknown wire
/// strings throw — there is no forward-compat opt-out for severity
/// (the 5-step ladder is intended to be stable).
/// </summary>
public sealed class SeverityJsonConverter : JsonConverter<Severity>
{
    public override Severity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                $"Expected string for severity, got {reader.TokenType}; map to NIP-REPUTATION-ENTRY-INVALID.");

        var s = reader.GetString();
        if (Severities.TryParse(s, out var sev))
            return sev;
        throw new JsonException(
            $"Unknown severity value '{s}'; map to NIP-REPUTATION-ENTRY-INVALID.");
    }

    public override void Write(Utf8JsonWriter writer, Severity value, JsonSerializerOptions options)
        => writer.WriteStringValue(Severities.ToWire(value));
}
