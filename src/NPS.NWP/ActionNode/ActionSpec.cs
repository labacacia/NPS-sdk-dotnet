// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace NPS.NWP.ActionNode;

/// <summary>
/// NWM action registry entry (NPS-2 §4.6). Describes one invocable operation.
/// </summary>
public sealed class ActionSpec
{
    /// <summary>Human-readable description of the operation.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// anchor_id of the parameter schema (used by Agents to validate <c>ActionFrame.Params</c>).
    /// </summary>
    [JsonPropertyName("params_anchor")]
    public string? ParamsAnchor { get; init; }

    /// <summary>
    /// anchor_id of the result schema (used as <c>CapsFrame.anchor_ref</c> on success).
    /// </summary>
    [JsonPropertyName("result_anchor")]
    public string? ResultAnchor { get; init; }

    /// <summary>
    /// Whether this action supports asynchronous execution
    /// (<c>ActionFrame.Async = true</c> is only allowed when <c>true</c>).
    /// </summary>
    public required bool Async { get; init; }

    /// <summary>Whether the action is idempotent (clients may safely retry).</summary>
    public bool? Idempotent { get; init; }

    /// <summary>Default timeout in milliseconds applied when <c>ActionFrame.TimeoutMs</c> is absent.</summary>
    [JsonPropertyName("timeout_ms_default")]
    public uint? TimeoutMsDefault { get; init; }

    /// <summary>Maximum timeout this action will honour. Requests above are clamped.</summary>
    [JsonPropertyName("timeout_ms_max")]
    public uint? TimeoutMsMax { get; init; }

    /// <summary>NIP capability required to invoke this action, e.g. <c>"nwp:invoke"</c>.</summary>
    [JsonPropertyName("required_capability")]
    public string? RequiredCapability { get; init; }
}
