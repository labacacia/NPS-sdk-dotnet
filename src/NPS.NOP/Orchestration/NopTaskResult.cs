// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NOP.Models;

namespace NPS.NOP.Orchestration;

/// <summary>
/// Final result returned by <see cref="INopOrchestrator.ExecuteAsync"/> (NPS-5 §5).
/// </summary>
public sealed class NopTaskResult
{
    /// <summary>Task identifier from the original <c>TaskFrame.TaskId</c>.</summary>
    public required string TaskId { get; init; }

    /// <summary>Terminal state: <see cref="TaskState.Completed"/>, <see cref="TaskState.Failed"/>, or <see cref="TaskState.Cancelled"/>.</summary>
    public required TaskState FinalState { get; init; }

    /// <summary>
    /// Aggregated result from all terminal nodes.
    /// <c>null</c> when <see cref="FinalState"/> is <see cref="TaskState.Failed"/> or all terminal nodes were skipped.
    /// </summary>
    public JsonElement? AggregatedResult { get; init; }

    /// <summary>Error code when <see cref="FinalState"/> is not <see cref="TaskState.Completed"/>.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error description.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Per-node results keyed by DAG node ID (only nodes that completed successfully).</summary>
    public IReadOnlyDictionary<string, JsonElement> NodeResults { get; init; }
        = new Dictionary<string, JsonElement>();

    // ── Factory helpers ───────────────────────────────────────────────────────

    public static NopTaskResult Success(
        string taskId,
        JsonElement? aggregatedResult,
        IReadOnlyDictionary<string, JsonElement> nodeResults) =>
        new()
        {
            TaskId           = taskId,
            FinalState       = TaskState.Completed,
            AggregatedResult = aggregatedResult,
            NodeResults      = nodeResults,
        };

    public static NopTaskResult Failure(string taskId, string errorCode, string errorMessage) =>
        new()
        {
            TaskId       = taskId,
            FinalState   = TaskState.Failed,
            ErrorCode    = errorCode,
            ErrorMessage = errorMessage,
        };

    public static NopTaskResult Cancelled(string taskId, string reason) =>
        new()
        {
            TaskId       = taskId,
            FinalState   = TaskState.Cancelled,
            ErrorCode    = NopErrorCodes.TaskCancelled,
            ErrorMessage = reason,
        };
}
