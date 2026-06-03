// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NWP.Anchor.Reputation;

namespace NPS.NWP.Anchor;

/// <summary>
/// Configuration for a single Anchor Node (NPS-AaaS §2). Anchor Nodes are
/// the stateless entry point for AaaS deployments: every <c>ActionFrame</c>
/// received at <c>/invoke</c> is translated to a NOP <c>TaskFrame</c> and
/// dispatched to the local orchestrator; the Anchor itself does not execute
/// business logic.
/// </summary>
public sealed class AnchorNodeOptions
{
    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>Node NID, e.g. <c>urn:nps:node:api.example.com:agent-service</c>.</summary>
    public required string NodeId { get; set; }

    /// <summary>Human-readable name shown in the NWM manifest.</summary>
    public string? DisplayName { get; set; }

    /// <summary>HTTP path prefix where the anchor listens, e.g. <c>"/gw"</c>.</summary>
    public required string PathPrefix { get; set; }

    // ── Actions ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Declared actions advertised in the NWM. Keys are <c>{domain}.{verb}</c>
    /// identifiers (NPS-2 §4.6). The router is responsible for producing a
    /// <c>TaskFrame</c> for each declared action.
    /// </summary>
    public required IReadOnlyDictionary<string, AnchorActionSpec> Actions { get; set; }

    // ── Auth ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c> (default for Anchor — NPS-AaaS §2.3 auth.required=true),
    /// requests without <c>X-NWP-Agent</c> are rejected with 401.
    /// </summary>
    public bool RequireAuth { get; set; } = true;

    /// <summary>
    /// Capabilities every consumer NID MUST declare. Published under
    /// <c>auth.required_scopes</c> in the NWM.
    /// </summary>
    public IReadOnlyList<string>? RequiredCapabilities { get; set; }

    /// <summary>
    /// When <c>true</c>, topology endpoints (<c>/query</c>, <c>/subscribe</c>) require
    /// the caller to declare <c>"topology:read"</c> in the <c>X-NWP-Capabilities</c>
    /// request header. Missing capability → 403 <c>NWP-TOPOLOGY-UNAUTHORIZED</c>
    /// (NPS-CR-0002 §12.4 authorization policy).
    /// </summary>
    public bool RequireTopologyCapability { get; set; } = false;

    // ── Timeouts ─────────────────────────────────────────────────────────────

    /// <summary>Default timeout when neither <see cref="AnchorActionSpec.TimeoutMsDefault"/>
    /// nor <c>ActionFrame.TimeoutMs</c> are set. Default 30000 ms.</summary>
    public uint DefaultTimeoutMs { get; set; } = 30_000;

    /// <summary>Hard cap: requests above this are clamped. Default 300000 ms.</summary>
    public uint MaxTimeoutMs { get; set; } = 300_000;

    // ── Rate limits ──────────────────────────────────────────────────────────

    /// <summary>
    /// Declarative rate limit block advertised in the NWM. Enforcement is
    /// performed by the registered <see cref="IAnchorRateLimiter"/>
    /// (in-memory default; swap for a distributed implementation in production).
    /// </summary>
    public AnchorRateLimits? RateLimits { get; set; }

    // ── Token budget ─────────────────────────────────────────────────────────

    /// <summary>Default token budget when <c>X-NWP-Budget</c> header is absent. 0 = unlimited.</summary>
    public uint DefaultTokenBudget { get; set; } = 0;

    /// <summary>
    /// Node-operator CGN cap per request (token-budget.md §7). 0 = unlimited.
    /// When non-zero, the effective budget is <c>min(CgnLimit, X-NWP-Budget)</c>.
    /// Published in the NWM under <c>token_budget.cgn_limit</c> so agents can
    /// discover the cap before sending requests.
    /// </summary>
    public uint CgnLimit { get; set; } = 0;

    // ── Auth enforcement ─────────────────────────────────────────────────────

    /// <summary>
    /// URL of the CA enrollment page advertised in the <c>hint</c> field when
    /// a request is rejected with <c>NWP-AUTH-ASSURANCE-TOO-LOW</c>
    /// (RFC-0005 §4.1.4 step 1). Optional; if omitted no hint is returned.
    /// Example: <c>"https://ca.example.com/enroll"</c>.
    /// </summary>
    public string? AssuranceHintUrl { get; set; }

    // ── Reputation policy ────────────────────────────────────────────────────

    /// <summary>
    /// NPS-RFC-0005 reputation policy for this Anchor Node. When non-null and
    /// <see cref="ReputationPolicy.Enabled"/> is <c>true</c>, the node queries
    /// the configured log sources and evaluates admission rules on every
    /// <c>/invoke</c> request. Published verbatim in the NWM under the
    /// <c>reputation_policy</c> key (RFC-0005 §4.5).
    /// <para>
    /// Enforcement requires an <see cref="IReputationPolicyEvaluator"/>
    /// registered in DI (defaults to <see cref="DefaultReputationPolicyEvaluator"/>
    /// when <c>AddAnchorNode</c> is used).
    /// </para>
    /// </summary>
    public ReputationPolicy? ReputationPolicy { get; set; }

    // ── Trust anchors ────────────────────────────────────────────────────────

    /// <summary>
    /// CA NID URNs that this node trusts as root certificate authorities
    /// (NPS-2 §4.1, NWP v0.13). Published in the NWM under <c>trust_anchors</c>
    /// so connecting Agents know which CA signed this node's certificate.
    /// Optional; omit for single-CA deployments.
    /// </summary>
    public IReadOnlyList<string>? TrustAnchors { get; set; }

    // ── Manifest versioning ──────────────────────────────────────────────────

    /// <summary>
    /// Monotonically incrementing manifest version counter (NWP v0.14 §4.1).
    /// Starts at 1; increment whenever the NWM structure changes.
    /// Published in the NWM as <c>manifest_version</c> and returned in the
    /// <c>X-NWM-Version</c> response header on every <c>GET /.nwm</c> request.
    /// Agents use it as the <c>If-None-Match</c> value for conditional requests.
    /// </summary>
    public uint ManifestVersion { get; set; } = 1;

    /// <summary>
    /// ISO 8601 UTC timestamp of the last NWM structural change (NWP v0.14 §4.1).
    /// Set this whenever you increment <see cref="ManifestVersion"/>.
    /// Published in the NWM as <c>manifest_updated_at</c>. Optional.
    /// </summary>
    public string? ManifestUpdatedAt { get; set; }

    // ── Observability ────────────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c> (default), the middleware auto-generates a
    /// W3C TraceContext <c>trace_id</c> / <c>span_id</c> pair per request and
    /// injects it into <c>TaskFrame.Context</c>. Disable if the host
    /// application already provides these via its own instrumentation pipeline.
    /// </summary>
    public bool AutoInjectTraceContext { get; set; } = true;
}
