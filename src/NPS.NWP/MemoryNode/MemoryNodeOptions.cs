// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.MemoryNode;

/// <summary>
/// Configuration for a single Memory Node instance (NPS-2 §2.1).
/// Register via <c>AddMemoryNode&lt;TProvider&gt;()</c> DI extension.
/// </summary>
public sealed class MemoryNodeOptions
{
    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Node NID, e.g. <c>urn:nps:node:api.example.com:products</c>.
    /// </summary>
    public required string NodeId { get; set; }

    /// <summary>Human-readable node name shown in the NWM manifest.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Schema describing the table this node exposes.</summary>
    public required MemoryNodeSchema Schema { get; set; }

    // ── Query limits ──────────────────────────────────────────────────────────

    /// <summary>Default page size when <c>QueryFrame.Limit</c> is absent. Default 20.</summary>
    public uint DefaultLimit { get; set; } = 20;

    /// <summary>Hard cap on <c>QueryFrame.Limit</c>. Requests above this are clamped. Default 1000.</summary>
    public uint MaxLimit { get; set; } = 1000;

    // ── Auth ──────────────────────────────────────────────────────────────────

    /// <summary>When <c>true</c>, requests without <c>X-NWP-Agent</c> are rejected with 401.</summary>
    public bool RequireAuth { get; set; } = false;

    // ── Token budget ──────────────────────────────────────────────────────────

    /// <summary>
    /// Default token budget applied when <c>X-NWP-Budget</c> header is absent.
    /// 0 = unlimited.
    /// </summary>
    public uint DefaultTokenBudget { get; set; } = 0;

    // ── Route prefix ──────────────────────────────────────────────────────────

    /// <summary>
    /// HTTP path prefix where this node listens, e.g. <c>"/products"</c>.
    /// The middleware appends sub-paths (<c>/.nwm</c>, <c>/.schema</c>, <c>/query</c>, <c>/stream</c>).
    /// </summary>
    public required string PathPrefix { get; set; }
}
