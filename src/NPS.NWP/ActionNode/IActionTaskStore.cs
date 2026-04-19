// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace NPS.NWP.ActionNode;

/// <summary>
/// Record of a single asynchronous action task. State machine (NPS-2 §7.2):
/// <c>Pending → Running → Completed / Failed / Cancelled</c>.
/// </summary>
public sealed class ActionTaskRecord
{
    public required string TaskId    { get; init; }
    public required string ActionId  { get; init; }
    public required string Status    { get; set; }  // pending | running | completed | failed | cancelled
    public          double? Progress  { get; set; }
    public required DateTime CreatedAt { get; init; }
    public          DateTime UpdatedAt { get; set; }
    public          string?  RequestId { get; init; }
    public          string?  AgentNid  { get; init; }
    public          JsonElement? Result { get; set; }
    public          JsonElement? Error  { get; set; }
}

/// <summary>
/// Storage for asynchronous action task state. The default implementation is
/// <see cref="InMemoryActionTaskStore"/>. Replace with a persistent store (Redis,
/// PostgreSQL) for multi-instance deployments.
/// </summary>
public interface IActionTaskStore
{
    /// <summary>Insert a new task in <c>pending</c> state. Returns the created record.</summary>
    ActionTaskRecord Create(string taskId, string actionId, string? requestId, string? agentNid);

    /// <summary>Look up a task by id, or <c>null</c> if unknown / expired.</summary>
    ActionTaskRecord? Get(string taskId);

    /// <summary>
    /// Atomically transition status. Returns <c>true</c> if the transition succeeded,
    /// <c>false</c> if the current status no longer matches <paramref name="expectedStatus"/>.
    /// </summary>
    bool TryTransition(string taskId, string expectedStatus, string newStatus);

    /// <summary>Record successful completion.</summary>
    bool Complete(string taskId, JsonElement? result);

    /// <summary>Record a failure. <paramref name="error"/> is a JSON element, usually
    /// <c>{ "code": "...", "message": "..." }</c>.</summary>
    bool Fail(string taskId, JsonElement error);

    /// <summary>Mark a running task cancelled. Returns <c>true</c> when the task existed
    /// and was not already in a terminal state.</summary>
    bool Cancel(string taskId);

    /// <summary>Update the <c>progress</c> field (optional).</summary>
    bool UpdateProgress(string taskId, double progress);

    /// <summary>Purge terminal-state records older than <paramref name="retention"/>.</summary>
    int PurgeExpired(TimeSpan retention, DateTime now);
}
