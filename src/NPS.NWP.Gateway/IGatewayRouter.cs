// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NWP.Frames;

namespace NPS.NWP.Gateway;

/// <summary>
/// Request-scoped context passed to <see cref="IGatewayRouter.BuildTaskAsync"/>.
/// </summary>
public sealed class GatewayRouteContext
{
    /// <summary>Action metadata declared in <see cref="GatewayNodeOptions.Actions"/>.</summary>
    public required GatewayActionSpec Spec { get; init; }

    /// <summary>Consumer Agent NID (value of <c>X-NWP-Agent</c>), or <c>null</c> if auth is off.</summary>
    public string? AgentNid { get; init; }

    /// <summary>Echo of <c>ActionFrame.RequestId</c> for tracing.</summary>
    public string? RequestId { get; init; }

    /// <summary>Effective timeout after clamping (see <see cref="GatewayNodeOptions"/>).</summary>
    public required uint EffectiveTimeoutMs { get; init; }

    /// <summary>Resolved priority (<c>"low"</c>/<c>"normal"</c>/<c>"high"</c>).</summary>
    public required string Priority { get; init; }

    /// <summary>
    /// Observability context (trace_id / span_id / baggage). The middleware
    /// auto-generates IDs when <see cref="GatewayNodeOptions.AutoInjectTraceContext"/>
    /// is <c>true</c> and the request has none; routers may override this.
    /// </summary>
    public TaskContext? TraceContext { get; init; }

    /// <summary>Token budget advertised by the consumer (<c>X-NWP-Budget</c>).</summary>
    public uint BudgetNpt { get; init; }
}

/// <summary>
/// Maps an inbound <see cref="ActionFrame"/> to a <see cref="TaskFrame"/> that
/// the local NOP orchestrator will execute. The router owns the
/// action-id → DAG mapping — keeping that knowledge out of the middleware
/// so the Gateway itself stays stateless and deployment-agnostic (NPS-AaaS §2.1).
/// </summary>
public interface IGatewayRouter
{
    /// <summary>
    /// Build a <see cref="TaskFrame"/> for the incoming action invocation.
    /// The middleware has already validated that <c>frame.ActionId</c> exists
    /// in <see cref="GatewayNodeOptions.Actions"/>, so implementations can
    /// focus on DAG shaping and parameter mapping.
    /// </summary>
    Task<TaskFrame> BuildTaskAsync(
        ActionFrame         frame,
        GatewayRouteContext ctx,
        CancellationToken   cancel = default);
}
