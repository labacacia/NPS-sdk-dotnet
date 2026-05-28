// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NPS.NIP;
using NPS.NIP.Reputation;
using NPS.NWP.Http;

namespace NPS.NWP.Anchor.Reputation;

/// <summary>
/// Default in-process <see cref="IReputationPolicyEvaluator"/> that queries
/// RFC-0004 reputation logs and applies admission rules in the order defined
/// by NPS-RFC-0005 §4.1.4:
/// <para>1. <c>min_assurance_level</c> check</para>
/// <para>2. In-process ban cache check</para>
/// <para>3. Log query (with per-NID result cache)</para>
/// <para>4. <c>ban_on</c> evaluation</para>
/// <para>5. <c>reject_on</c> evaluation</para>
/// <para>6. <c>throttle_on</c> evaluation</para>
/// <para>7. Accept</para>
///
/// <para>Ban state and query cache are in-process. A Node restart clears both
/// (RFC-0005 §4.1.2). Inject a custom <see cref="IReputationPolicyEvaluator"/>
/// for persistent ban storage or custom log transport.</para>
/// </summary>
public sealed class DefaultReputationPolicyEvaluator : IReputationPolicyEvaluator, IDisposable
{
    private readonly ILogger              _log;
    private readonly IHttpClientFactory?  _httpClientFactory;

    // Per source URL → owned ReputationLogClient (lazy, keyed by base URL).
    private readonly ConcurrentDictionary<string, ReputationLogClient> _clients = new();

    // NID → (entries snapshot, absolute expiry)
    private readonly ConcurrentDictionary<string, (IReadOnlyList<ReputationLogEntry> entries, DateTimeOffset expiry)>
        _queryCache = new();

    // NID → ban expiry (cleared on restart per spec)
    private readonly ConcurrentDictionary<string, DateTimeOffset> _banCache = new();

    public DefaultReputationPolicyEvaluator(
        ILogger<DefaultReputationPolicyEvaluator> logger,
        IHttpClientFactory?                       httpClientFactory = null)
    {
        _log               = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ReputationDecision> EvaluateAsync(
        string            requesterNid,
        AssuranceLevel    assuranceLevel,
        ReputationPolicy  policy,
        CancellationToken ct = default)
    {
        if (!policy.Enabled)
            return Accept;

        var now = DateTimeOffset.UtcNow;

        // Step 1: min_assurance_level — reject before any log query.
        var minLevel = AssuranceLevels.TryParse(policy.MinAssuranceLevel, out var min)
            ? min : AssuranceLevel.Anonymous;
        if (assuranceLevel < minLevel)
            return new ReputationDecision(
                ReputationOutcome.Reject, null,
                NwpErrorCodes.AuthAssuranceTooLow);

        // Step 2: Ban cache — skip log query if NID is already banned.
        if (_banCache.TryGetValue(requesterNid, out var banExpiry) && banExpiry > now)
            return new ReputationDecision(
                ReputationOutcome.Ban, null,
                NwpErrorCodes.ReputationBanned,
                BanExpiresAt: banExpiry);

        // Step 3: Log query.
        var (logAvailable, entries) = await FetchEntriesAsync(requesterNid, policy, now, ct);
        if (!logAvailable && policy.OnLogUnavailable.Equals("deny", StringComparison.OrdinalIgnoreCase))
            return new ReputationDecision(
                ReputationOutcome.Reject, null,
                NwpErrorCodes.NodeUnavailable);

        // Steps 4–6: each rule set is evaluated independently — no short-circuit between them.
        // A NID that matches both ban_on and reject_on receives the ban outcome (step 4 wins).

        // Step 4: ban_on
        var (banRule, banEntry) = FirstMatchingRule(policy.BanOn, entries, now);
        if (banRule is not null && banEntry is not null)
        {
            var expires = now.AddSeconds(policy.BanTtlSeconds);
            _banCache[requesterNid] = expires;
            return new ReputationDecision(
                ReputationOutcome.Ban, banRule,
                NwpErrorCodes.ReputationBanned,
                MatchedIncident: GetWireIncident(banEntry),
                MatchedSeverity: Severities.ToWire(banEntry.Severity),
                BanExpiresAt:    expires);
        }

        // Step 5: reject_on
        var (rejectRule, rejectEntry) = FirstMatchingRule(policy.RejectOn, entries, now);
        if (rejectRule is not null && rejectEntry is not null)
            return new ReputationDecision(
                ReputationOutcome.Reject, rejectRule,
                NwpErrorCodes.ReputationRejected,
                MatchedIncident: GetWireIncident(rejectEntry),
                MatchedSeverity: Severities.ToWire(rejectEntry.Severity));

        // Step 6: throttle_on
        var (throttleRule, throttleEntry) = FirstMatchingRule(policy.ThrottleOn, entries, now);
        if (throttleRule is not null && throttleEntry is not null)
            return new ReputationDecision(
                ReputationOutcome.Throttle, throttleRule,
                NwpErrorCodes.ReputationThrottled,
                MatchedIncident: GetWireIncident(throttleEntry),
                MatchedSeverity: Severities.ToWire(throttleEntry.Severity));

        // Step 7: Accept
        return Accept;
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly ReputationDecision Accept =
        new(ReputationOutcome.Accept, null, null);

    private static (ReputationPolicyRule? rule, ReputationLogEntry? entry) FirstMatchingRule(
        IReadOnlyList<ReputationPolicyRule> rules,
        IReadOnlyList<ReputationLogEntry>   entries,
        DateTimeOffset                      now)
    {
        foreach (var rule in rules)
        {
            uint matched = 0;
            ReputationLogEntry? first = null;
            foreach (var e in entries)
            {
                if (!rule.Matches(e, now)) continue;
                first ??= e;
                if (++matched >= rule.Count)
                    return (rule, first);
            }
        }
        return (null, null);
    }

    private static string? GetWireIncident(ReputationLogEntry entry)
        => entry.Incident == IncidentType.Other
            ? entry.IncidentRaw
            : IncidentTypes.ToWire(entry.Incident);

    private async Task<(bool available, IReadOnlyList<ReputationLogEntry> entries)> FetchEntriesAsync(
        string            nid,
        ReputationPolicy  policy,
        DateTimeOffset    now,
        CancellationToken ct)
    {
        // Check live cache.
        if (policy.CacheTtlSeconds > 0 &&
            _queryCache.TryGetValue(nid, out var cached) &&
            cached.expiry > now)
            return (true, cached.entries);

        // Try log sources in order; first success wins.
        foreach (var source in policy.LogSources)
        {
            try
            {
                var client = GetOrCreateClient(source);
                var results = await client.QueryAsync(nid: nid, ct: ct);

                if (policy.CacheTtlSeconds > 0)
                    _queryCache[nid] = (results, now.AddSeconds(policy.CacheTtlSeconds));

                return (true, results);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Reputation log source {Source} unavailable for NID {Nid}.",
                    source, nid);
            }
        }

        // All sources failed — fall back to stale cache when allowed.
        if (_queryCache.TryGetValue(nid, out var stale))
        {
            _log.LogWarning(
                "Using stale reputation cache for NID {Nid} (all log sources unavailable).", nid);
            return (false, stale.entries);
        }

        return (false, []);
    }

    private ReputationLogClient GetOrCreateClient(string baseUrl)
    {
        return _clients.GetOrAdd(baseUrl, url =>
        {
            if (_httpClientFactory is not null)
            {
                var http = _httpClientFactory.CreateClient(nameof(DefaultReputationPolicyEvaluator));
                http.BaseAddress = new Uri(url.TrimEnd('/') + '/');
                return new ReputationLogClient(http);
            }
            return new ReputationLogClient(url);
        });
    }
}
