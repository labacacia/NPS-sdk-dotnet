// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using NPS.NIP.Reputation;

namespace NPS.NWP.Anchor.Reputation;

/// <summary>
/// Node-operator policy controlling how an Anchor Node consults NPS-RFC-0004
/// reputation logs to make admission decisions (NPS-RFC-0005 §4.1).
/// Published verbatim at <c>/.nwm</c> under the <c>reputation_policy</c> key.
/// </summary>
public sealed class ReputationPolicy
{
    /// <summary>When <c>false</c>, the policy is parsed but enforcement is skipped. Default <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Ordered list of RFC-0004 compliant log operator base URLs. Queried
    /// in order; first successful response wins (NPS-RFC-0005 §4.1.2).
    /// </summary>
    [JsonPropertyName("log_sources")]
    public IReadOnlyList<string> LogSources { get; set; } = [];

    /// <summary>
    /// Minimum RFC-0003 assurance level. Requests below this level are
    /// rejected with <c>NWP-AUTH-ASSURANCE-TOO-LOW</c> before log queries.
    /// One of <c>"anonymous"</c> (default) / <c>"attested"</c> / <c>"verified"</c>.
    /// </summary>
    [JsonPropertyName("min_assurance_level")]
    public string MinAssuranceLevel { get; set; } = "anonymous";

    /// <summary>How long to cache a log query result per NID, in seconds. 0 = no caching. Default 300.</summary>
    [JsonPropertyName("cache_ttl_seconds")]
    public uint CacheTtlSeconds { get; set; } = 300;

    /// <summary>
    /// How long a NID remains banned after a <c>ban_on</c> rule fires, in seconds.
    /// Ban state is in-process; a Node restart clears bans. Default 3600.
    /// </summary>
    [JsonPropertyName("ban_ttl_seconds")]
    public uint BanTtlSeconds { get; set; } = 3600;

    /// <summary>
    /// Policy applied when all configured <see cref="LogSources"/> are unreachable.
    /// <c>"allow"</c> (default) lets the request proceed; <c>"deny"</c> rejects it
    /// (availability trade-off — see RFC-0005 §5.1).
    /// </summary>
    [JsonPropertyName("on_log_unavailable")]
    public string OnLogUnavailable { get; set; } = "allow";

    /// <summary>Rules that trigger rate-limiting (HTTP 429). All rules are evaluated; any match fires.</summary>
    [JsonPropertyName("throttle_on")]
    public IReadOnlyList<ReputationPolicyRule> ThrottleOn { get; set; } = [];

    /// <summary>Rules that trigger hard rejection (HTTP 403). Evaluated after <c>ban_on</c>; any match fires.</summary>
    [JsonPropertyName("reject_on")]
    public IReadOnlyList<ReputationPolicyRule> RejectOn { get; set; } = [];

    /// <summary>Rules that trigger a time-limited ban (HTTP 403 + ban cache entry). Evaluated first; any match fires.</summary>
    [JsonPropertyName("ban_on")]
    public IReadOnlyList<ReputationPolicyRule> BanOn { get; set; } = [];
}

/// <summary>
/// A single admission rule within a <see cref="ReputationPolicy"/> rule set
/// (NPS-RFC-0005 §4.1.3).
/// </summary>
public sealed class ReputationPolicyRule
{
    /// <summary>
    /// RFC-0004 §4.2 incident type (kebab-case wire string), or <c>"*"</c> to match any. Required.
    /// </summary>
    public required string Incident { get; set; }

    /// <summary>
    /// Severity predicate: <c>">=&lt;level&gt;"</c> or exact <c>"&lt;level&gt;"</c>.
    /// Scale (ascending): <c>info</c> / <c>minor</c> / <c>moderate</c> / <c>major</c> / <c>critical</c>.
    /// </summary>
    public required string Severity { get; set; }

    /// <summary>Only consider entries within this many days of now. Absent = all time.</summary>
    [JsonPropertyName("within_days")]
    public uint? WithinDays { get; set; }

    /// <summary>Minimum number of matching entries required to fire the rule. Default 1.</summary>
    public uint Count { get; set; } = 1;

    internal (NPS.NIP.Reputation.Severity threshold, bool atLeast) ParseSeverityPredicate()
    {
        var s = Severity.Trim();
        bool atLeast;
        string levelStr;
        if (s.StartsWith(">=", StringComparison.Ordinal))
        {
            atLeast  = true;
            levelStr = s[2..];
        }
        else
        {
            atLeast  = false;
            levelStr = s;
        }
        if (!Severities.TryParse(levelStr, out var level))
            throw new InvalidOperationException(
                $"ReputationPolicyRule severity '{s}' is not valid. Use: info/minor/moderate/major/critical (optionally prefixed with '>=').");
        return (level, atLeast);
    }

    internal bool Matches(ReputationLogEntry entry, DateTimeOffset now)
    {
        // Incident predicate
        if (Incident != "*")
        {
            var wireIncident = entry.Incident == IncidentType.Other
                ? entry.IncidentRaw
                : IncidentTypes.ToWire(entry.Incident);
            if (!string.Equals(wireIncident, Incident, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Severity predicate
        var (threshold, atLeast) = ParseSeverityPredicate();
        if (atLeast)
        {
            if (entry.Severity < threshold) return false;
        }
        else
        {
            if (entry.Severity != threshold) return false;
        }

        // Time window
        if (WithinDays is { } days)
        {
            if (!DateTimeOffset.TryParse(entry.Timestamp, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                return false;
            if ((now - ts).TotalDays > days) return false;
        }

        return true;
    }
}
