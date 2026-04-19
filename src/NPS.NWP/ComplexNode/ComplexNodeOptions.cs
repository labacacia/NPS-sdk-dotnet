// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NWP.ActionNode;
using NPS.NWP.MemoryNode;

namespace NPS.NWP.ComplexNode;

/// <summary>
/// Configuration for a single Complex Node (NPS-2 §2.1, §11). A Complex Node mixes
/// Memory-Node-style data queries, Action-Node-style operations, and a declared graph
/// of <i>child</i> NWP nodes that Agents can traverse via the <c>X-NWP-Depth</c> header.
///
/// <para>
/// Register via <c>AddComplexNode&lt;TProvider&gt;()</c> and mount via
/// <c>UseComplexNode&lt;TProvider&gt;()</c>.
/// </para>
/// </summary>
public sealed class ComplexNodeOptions
{
    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>Node NID, e.g. <c>urn:nps:node:api.example.com:orders</c>.</summary>
    public required string NodeId { get; set; }

    /// <summary>Human-readable node name shown in the NWM manifest.</summary>
    public string? DisplayName { get; set; }

    /// <summary>HTTP path prefix where the node listens, e.g. <c>"/orders"</c>.</summary>
    public required string PathPrefix { get; set; }

    // ── Local behaviours (optional) ──────────────────────────────────────────

    /// <summary>
    /// Optional row schema exposing <c>/query</c> + <c>/.schema</c> (Memory-Node-like).
    /// When <c>null</c>, <c>/query</c> either returns an empty CapsFrame (if graph
    /// traversal yielded no refs) or only the traversal result.
    /// </summary>
    public MemoryNodeSchema? Schema { get; set; }

    /// <summary>
    /// Action registry (may be empty). Follows the Action Node conventions; the reserved
    /// ids <c>system.task.status</c> / <c>system.task.cancel</c> MUST NOT be registered here.
    /// </summary>
    public IReadOnlyDictionary<string, ActionSpec> Actions { get; set; }
        = new Dictionary<string, ActionSpec>();

    // ── Graph ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Child node references declared in the NWM (NPS-2 §11). Each entry maps a
    /// semantic relationship (<c>"user"</c>, <c>"payment"</c>) to a child NWP address.
    /// </summary>
    public IReadOnlyList<ComplexGraphRef> Graph { get; set; } = Array.Empty<ComplexGraphRef>();

    /// <summary>
    /// Maximum traversal depth the node advertises and honours. Clamped to
    /// <see cref="AbsoluteMaxDepth"/>. Default 2.
    /// </summary>
    public uint GraphMaxDepth { get; set; } = 2;

    /// <summary>Absolute ceiling per NPS-2 §11 (<c>X-NWP-Depth</c> upper bound). 5.</summary>
    public const uint AbsoluteMaxDepth = 5;

    /// <summary>
    /// URL prefixes that child <c>nwp://</c> addresses MUST start with. Empty means
    /// "no allowlist" — DISCOURAGED: NPS-2 §13.2 requires an explicit allowlist.
    /// </summary>
    public IReadOnlyList<string> AllowedChildUrlPrefixes { get; set; } = Array.Empty<string>();

    /// <summary>
    /// When <c>true</c>, child hosts that resolve to loopback / RFC1918 / link-local
    /// addresses are rejected before the outbound HTTP call. Default <c>true</c>.
    /// </summary>
    public bool RejectPrivateChildUrls { get; set; } = true;

    // ── Auth / limits ────────────────────────────────────────────────────────

    /// <summary>When <c>true</c>, requests without <c>X-NWP-Agent</c> are rejected with 401.</summary>
    public bool RequireAuth { get; set; } = false;

    /// <summary>Default page size when <see cref="NPS.NWP.Frames.QueryFrame.Limit"/> is absent. 20.</summary>
    public uint DefaultLimit { get; set; } = 20;

    /// <summary>Hard cap on <see cref="NPS.NWP.Frames.QueryFrame.Limit"/>. 1000.</summary>
    public uint MaxLimit { get; set; } = 1000;

    /// <summary>Default action timeout (ms) when none specified. 5000.</summary>
    public uint DefaultTimeoutMs { get; set; } = 5_000;

    /// <summary>Hard cap on action timeout (ms). 300000.</summary>
    public uint MaxTimeoutMs { get; set; } = 300_000;

    /// <summary>Timeout for outbound child-node fetches during graph expansion. 10 s.</summary>
    public TimeSpan ChildFetchTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Child node reference declared in <see cref="ComplexNodeOptions.Graph"/>
/// (NPS-2 §11).
/// </summary>
/// <param name="Rel">Semantic relationship label, e.g. <c>"user"</c>, <c>"payment"</c>.</param>
/// <param name="NodeUrl">Absolute child URL, e.g. <c>https://api.myapp.com/users</c>.
///   Exposed in the NWM as <c>nwp://...</c> but fetched over HTTPS.</param>
public sealed record ComplexGraphRef(string Rel, string NodeUrl);
