// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NOP.Models;

namespace NPS.NOP.Orchestration;

/// <summary>
/// Persistence abstraction for NOP task and subtask state (NPS-5 §5).
/// </summary>
public interface INopTaskStore
{
    /// <summary>Persists a new task record. Throws if <see cref="NopTaskRecord.TaskId"/> already exists.</summary>
    Task SaveAsync(NopTaskRecord record, CancellationToken ct = default);

    /// <summary>Returns the task record, or <c>null</c> if not found.</summary>
    Task<NopTaskRecord?> GetAsync(string taskId, CancellationToken ct = default);

    /// <summary>Updates the overall task state.</summary>
    Task UpdateStateAsync(string taskId, TaskState state, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a subtask record within the task.
    /// </summary>
    Task UpdateSubtaskAsync(
        string taskId,
        string nodeId,
        string subtaskId,
        TaskState state,
        JsonElement? result    = null,
        string?      errorCode = null,
        string?      errorMsg  = null,
        int          attempt   = 1,
        CancellationToken ct   = default);
}
