// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

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

    // ── Observability ────────────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c> (default), the middleware auto-generates a
    /// W3C TraceContext <c>trace_id</c> / <c>span_id</c> pair per request and
    /// injects it into <c>TaskFrame.Context</c>. Disable if the host
    /// application already provides these via its own instrumentation pipeline.
    /// </summary>
    public bool AutoInjectTraceContext { get; set; } = true;
}
