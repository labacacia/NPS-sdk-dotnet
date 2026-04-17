// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NOP.Frames;
using NPS.NOP.Models;

namespace NPS.NOP.Orchestration;

/// <summary>
/// Persistent record of a running or completed NOP task.
/// </summary>
public sealed class NopTaskRecord
{
    public required string    TaskId    { get; init; }
    public required TaskFrame Frame     { get; init; }
    public required DateTime  StartedAt { get; init; }

    /// <summary>Overall task state (thread-safe via volatile field).</summary>
    public volatile TaskState State;

    public DateTime? CompletedAt { get; set; }

    /// <summary>Per-node subtask records, keyed by DAG node ID.</summary>
    public Dictionary<string, NopSubtaskRecord> Subtasks { get; init; } = new();
}

/// <summary>
/// State and result for a single DAG node (subtask).
/// </summary>
public sealed class NopSubtaskRecord
{
    public required string    NodeId    { get; init; }
    public required string    SubtaskId { get; init; }

    public volatile TaskState State;

    /// <summary>Resolved output (set when State == Completed).</summary>
    public JsonElement? Result { get; set; }

    /// <summary>Error code (set when State == Failed).</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable error message.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of execution attempts made (1 = first try, 2 = first retry, …).</summary>
    public int AttemptCount { get; set; }
}
