// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;

namespace NPS.NOP.Storage;

/// <summary>
/// Volatile, in-memory implementation of <see cref="INopTaskStore"/>.
/// Suitable for testing and single-process deployments.
/// Not durable across restarts.
/// </summary>
public sealed class InMemoryNopTaskStore : INopTaskStore
{
    private readonly ConcurrentDictionary<string, NopTaskRecord> _tasks = new();

    public Task SaveAsync(NopTaskRecord record, CancellationToken ct = default)
    {
        if (!_tasks.TryAdd(record.TaskId, record))
            throw new InvalidOperationException($"Task already exists: {record.TaskId}");
        return Task.CompletedTask;
    }

    public Task<NopTaskRecord?> GetAsync(string taskId, CancellationToken ct = default)
        => Task.FromResult(_tasks.GetValueOrDefault(taskId));

    public Task UpdateStateAsync(string taskId, TaskState state, CancellationToken ct = default)
    {
        if (_tasks.TryGetValue(taskId, out var rec))
            rec.State = state;
        return Task.CompletedTask;
    }

    public Task UpdateSubtaskAsync(
        string taskId,
        string nodeId,
        string subtaskId,
        TaskState state,
        JsonElement? result    = null,
        string?      errorCode = null,
        string?      errorMsg  = null,
        int          attempt   = 1,
        CancellationToken ct   = default)
    {
        if (!_tasks.TryGetValue(taskId, out var rec)) return Task.CompletedTask;

        lock (rec.Subtasks)
        {
            if (!rec.Subtasks.TryGetValue(nodeId, out var sub))
            {
                sub = new NopSubtaskRecord { NodeId = nodeId, SubtaskId = subtaskId };
                rec.Subtasks[nodeId] = sub;
            }

            sub.State        = state;
            sub.AttemptCount = attempt;
            if (result.HasValue)    sub.Result       = result.Value;
            if (errorCode is not null) sub.ErrorCode = errorCode;
            if (errorMsg  is not null) sub.ErrorMessage = errorMsg;
        }
        return Task.CompletedTask;
    }
}
