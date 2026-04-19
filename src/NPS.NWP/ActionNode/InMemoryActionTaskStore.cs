// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;

namespace NPS.NWP.ActionNode;

/// <summary>
/// Thread-safe, in-process task store suitable for single-instance deployments and tests.
/// Replace with a persistent store for multi-instance Action Nodes.
/// </summary>
public sealed class InMemoryActionTaskStore : IActionTaskStore
{
    private readonly ConcurrentDictionary<string, ActionTaskRecord> _tasks = new();

    /// <summary>Injectable clock, defaults to <see cref="DateTime.UtcNow"/>.</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    public ActionTaskRecord Create(string taskId, string actionId, string? requestId, string? agentNid)
    {
        var now = Clock();
        var rec = new ActionTaskRecord
        {
            TaskId    = taskId,
            ActionId  = actionId,
            Status    = "pending",
            CreatedAt = now,
            UpdatedAt = now,
            RequestId = requestId,
            AgentNid  = agentNid,
        };
        if (!_tasks.TryAdd(taskId, rec))
            throw new InvalidOperationException($"Task id collision: {taskId}");
        return rec;
    }

    public ActionTaskRecord? Get(string taskId) =>
        _tasks.TryGetValue(taskId, out var r) ? r : null;

    public bool TryTransition(string taskId, string expectedStatus, string newStatus)
    {
        if (!_tasks.TryGetValue(taskId, out var rec)) return false;
        lock (rec)
        {
            if (rec.Status != expectedStatus) return false;
            rec.Status    = newStatus;
            rec.UpdatedAt = Clock();
            return true;
        }
    }

    public bool Complete(string taskId, JsonElement? result)
    {
        if (!_tasks.TryGetValue(taskId, out var rec)) return false;
        lock (rec)
        {
            if (rec.Status is "completed" or "failed" or "cancelled") return false;
            rec.Status    = "completed";
            rec.Result    = result;
            rec.Progress  = 1.0;
            rec.UpdatedAt = Clock();
            return true;
        }
    }

    public bool Fail(string taskId, JsonElement error)
    {
        if (!_tasks.TryGetValue(taskId, out var rec)) return false;
        lock (rec)
        {
            if (rec.Status is "completed" or "failed" or "cancelled") return false;
            rec.Status    = "failed";
            rec.Error     = error;
            rec.UpdatedAt = Clock();
            return true;
        }
    }

    public bool Cancel(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var rec)) return false;
        lock (rec)
        {
            if (rec.Status is "completed" or "failed" or "cancelled") return false;
            rec.Status    = "cancelled";
            rec.UpdatedAt = Clock();
            return true;
        }
    }

    public bool UpdateProgress(string taskId, double progress)
    {
        if (!_tasks.TryGetValue(taskId, out var rec)) return false;
        lock (rec)
        {
            rec.Progress  = Math.Clamp(progress, 0.0, 1.0);
            rec.UpdatedAt = Clock();
            return true;
        }
    }

    public int PurgeExpired(TimeSpan retention, DateTime now)
    {
        var cutoff = now - retention;
        var purged = 0;
        foreach (var kv in _tasks)
        {
            var rec = kv.Value;
            if (rec.Status is "completed" or "failed" or "cancelled" && rec.UpdatedAt < cutoff)
            {
                if (_tasks.TryRemove(kv.Key, out _)) purged++;
            }
        }
        return purged;
    }
}
