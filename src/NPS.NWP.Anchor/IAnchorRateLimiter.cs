// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.Anchor;

/// <summary>
/// Outcome of a <see cref="IAnchorRateLimiter.TryAcquire"/> call.
/// </summary>
/// <param name="Allowed">
/// <c>true</c> when the request fits within every declared limit and a token
/// has been debited from the bucket; <c>false</c> otherwise.
/// </param>
/// <param name="Reason">
/// Human-readable reason when <see cref="Allowed"/> is <c>false</c>
/// (e.g. <c>"requests_per_minute exceeded"</c>). Surfaced in the error payload.
/// </param>
/// <param name="RetryAfterSeconds">
/// Suggested Retry-After (seconds). Populated on rejection when a window-based
/// limit caused the denial.
/// </param>
public readonly record struct AnchorRateLimitResult(
    bool    Allowed,
    string? Reason            = null,
    int?    RetryAfterSeconds = null);

/// <summary>
/// Per-consumer rate-limit gate for Anchor Nodes (NPS-AaaS §2.3,
/// <c>rate_limits</c> block). Implementations MUST be thread-safe.
///
/// <para>
/// The in-memory default (<see cref="InMemoryAnchorRateLimiter"/>) is
/// adequate for single-instance deployments; multi-instance anchors should
/// register a Redis- or Dragonfly-backed implementation.
/// </para>
/// </summary>
public interface IAnchorRateLimiter
{
    /// <summary>
    /// Attempt to acquire a request slot for the given consumer. Returns
    /// <c>AnchorRateLimitResult(Allowed=true)</c> when the request fits all
    /// limits (requests/min, concurrent, CGN/hour).
    /// </summary>
    /// <param name="consumerKey">Consumer identifier — typically the Agent NID.</param>
    /// <param name="cgnCost">Estimated CGN cost to deduct from the hourly bucket.</param>
    /// <param name="limits">Rate limit values from the node options; may be <c>null</c>.</param>
    AnchorRateLimitResult TryAcquire(
        string             consumerKey,
        uint               cgnCost,
        AnchorRateLimits? limits);

    /// <summary>
    /// Release a concurrent slot previously acquired by <see cref="TryAcquire"/>.
    /// Safe to call even when the acquire was short-circuited by an
    /// unlimited <see cref="AnchorRateLimits.MaxConcurrent"/>.
    /// </summary>
    void Release(string consumerKey);
}
