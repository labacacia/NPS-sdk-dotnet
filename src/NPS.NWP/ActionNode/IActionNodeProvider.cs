// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NWP.Frames;

namespace NPS.NWP.ActionNode;

/// <summary>
/// Result of a single action execution.
/// </summary>
public sealed class ActionExecutionResult
{
    /// <summary>
    /// Action output. Serialised into <c>CapsFrame.Data</c> (single element) for the
    /// response. <c>null</c> is allowed for side-effecting actions with no return payload.
    /// </summary>
    public JsonElement? Result { get; init; }

    /// <summary>
    /// Optional anchor_id for the response schema. Overrides <see cref="ActionSpec.ResultAnchor"/>
    /// on the NWM entry when the provider returns a polymorphic shape.
    /// </summary>
    public string? AnchorRef { get; init; }

    /// <summary>Approximate token count of the serialised result. 0 when unknown.</summary>
    public uint TokenEst { get; init; }
}

/// <summary>
/// Execution context passed to <see cref="IActionNodeProvider.ExecuteAsync"/>.
/// Carries the request metadata so providers can make scheduling and auditing decisions.
/// </summary>
public sealed class ActionContext
{
    /// <summary>NID of the calling agent from <c>X-NWP-Agent</c> header, or <c>null</c>.</summary>
    public required string? AgentNid { get; init; }

    /// <summary><c>RequestId</c> echoed from <see cref="ActionFrame"/>, or <c>null</c>.</summary>
    public required string? RequestId { get; init; }

    /// <summary>Task id when executing asynchronously, <c>null</c> for sync calls.</summary>
    public string? TaskId { get; init; }

    /// <summary>Matching NWM entry for the <c>action_id</c>.</summary>
    public required ActionSpec Spec { get; init; }

    /// <summary>Effective timeout after clamping. Providers SHOULD respect it.</summary>
    public required uint TimeoutMs { get; init; }

    /// <summary>Priority hint from <see cref="ActionFrame.Priority"/>, or <c>"normal"</c>.</summary>
    public required string Priority { get; init; }
}

/// <summary>
/// Implement this to expose a set of actions on an Action Node. All <c>action_id</c> values
/// declared in <see cref="ActionNodeOptions.Actions"/> MUST be handled.
/// </summary>
public interface IActionNodeProvider
{
    /// <summary>
    /// Execute one action. May be invoked either synchronously (the caller awaits the
    /// result) or from the background async task runner (in which case
    /// <see cref="ActionContext.TaskId"/> is non-null).
    /// </summary>
    Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame      frame,
        ActionContext    context,
        CancellationToken ct = default);
}
