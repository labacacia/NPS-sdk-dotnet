// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace NPS.NWP.Anchor;

/// <summary>
/// Declarative description of one action exposed by a Anchor Node
/// (NPS-AaaS §2.3). The anchor advertises these entries in its NWM so that
/// consumers can introspect the service catalogue; the router then maps each
/// <c>action_id</c> to a concrete <c>TaskFrame</c> when the action is invoked.
/// </summary>
public sealed class AnchorActionSpec
{
    /// <summary>Human-readable description of the operation.</summary>
    public string? Description { get; init; }

    /// <summary><c>anchor_id</c> of the parameter schema for consumer validation.</summary>
    [JsonPropertyName("params_anchor")]
    public string? ParamsAnchor { get; init; }

    /// <summary><c>anchor_id</c> of the result schema. Surfaced as <c>CapsFrame.anchor_ref</c> on success.</summary>
    [JsonPropertyName("result_anchor")]
    public string? ResultAnchor { get; init; }

    /// <summary>
    /// When <c>true</c>, consumers may set <c>ActionFrame.Async = true</c>
    /// (anchor fires-and-forgets the orchestration).
    /// </summary>
    public required bool Async { get; init; }

    /// <summary>Hint of NPT cost, advertised via the NWM.</summary>
    [JsonPropertyName("estimated_npt")]
    public uint? EstimatedNpt { get; init; }

    /// <summary>Default timeout applied when <c>ActionFrame.TimeoutMs</c> is absent.</summary>
    [JsonPropertyName("timeout_ms_default")]
    public uint? TimeoutMsDefault { get; init; }

    /// <summary>Maximum timeout this action will honour. Requests above are clamped.</summary>
    [JsonPropertyName("timeout_ms_max")]
    public uint? TimeoutMsMax { get; init; }

    /// <summary>NIP capability required to invoke this action, e.g. <c>"nwp:invoke"</c>.</summary>
    [JsonPropertyName("required_capability")]
    public string? RequiredCapability { get; init; }
}
