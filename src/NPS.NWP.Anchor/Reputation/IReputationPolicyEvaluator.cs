// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP;

namespace NPS.NWP.Anchor.Reputation;

/// <summary>
/// Evaluates an incoming request NID against the node-configured
/// <see cref="ReputationPolicy"/> and returns an admission decision
/// (NPS-RFC-0005 §4.3).
/// </summary>
public interface IReputationPolicyEvaluator
{
    /// <param name="requesterNid">NID extracted from <c>X-NWP-Agent</c>.</param>
    /// <param name="assuranceLevel">
    /// Verified assurance level from the Agent's IdentFrame. Pass
    /// <see cref="AssuranceLevel.Anonymous"/> when IdentFrame verification
    /// is not yet performed by the caller.
    /// </param>
    /// <param name="policy">The node-configured <see cref="ReputationPolicy"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ReputationDecision"/> with <see cref="ReputationDecision.Outcome"/>
    /// indicating the admission outcome and the matching rule on non-Accept results.
    /// </returns>
    Task<ReputationDecision> EvaluateAsync(
        string            requesterNid,
        AssuranceLevel    assuranceLevel,
        ReputationPolicy  policy,
        CancellationToken ct = default);
}

/// <summary>Admission outcome from a reputation policy evaluation (NPS-RFC-0005 §4.3).</summary>
public enum ReputationOutcome
{
    /// <summary>Request admitted — proceed to normal handling. Sets <c>X-NWP-Reputation-Status: clean</c>.</summary>
    Accept,

    /// <summary>Request rate-limited — return HTTP 429 with <c>Retry-After: 60</c>.</summary>
    Throttle,

    /// <summary>Request rejected by a <c>reject_on</c> rule — return HTTP 403.</summary>
    Reject,

    /// <summary>Request rejected and NID temporarily banned — return HTTP 403 + <c>X-NWP-Ban-Expires</c>.</summary>
    Ban,
}

/// <summary>
/// Result returned by <see cref="IReputationPolicyEvaluator.EvaluateAsync"/>
/// (NPS-RFC-0005 §4.3).
/// </summary>
public sealed record ReputationDecision(
    ReputationOutcome     Outcome,
    ReputationPolicyRule? MatchedRule,
    string?               ErrorCode,
    string?               MatchedIncident = null,
    string?               MatchedSeverity = null,
    DateTimeOffset?       BanExpiresAt    = null);
