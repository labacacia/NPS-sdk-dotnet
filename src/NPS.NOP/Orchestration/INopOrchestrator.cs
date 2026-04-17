// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NOP.Frames;
using NPS.NOP.Models;

namespace NPS.NOP.Orchestration;

/// <summary>
/// Core NOP orchestrator contract (NPS-5 §3, §5).
/// <para>
/// Accepts a <see cref="TaskFrame"/>, executes its DAG by dispatching
/// <see cref="DelegateFrame"/>s to Worker Agents via <see cref="INopWorkerClient"/>,
/// and returns a <see cref="NopTaskResult"/> when the task reaches a terminal state.
/// </para>
/// </summary>
public interface INopOrchestrator
{
    /// <summary>
    /// Executes the full task lifecycle:
    /// validate → (preflight) → run DAG → aggregate → (callback).
    /// Blocks until the task reaches a terminal state.
    /// </summary>
    Task<NopTaskResult> ExecuteAsync(TaskFrame task, CancellationToken ct = default);

    /// <summary>
    /// Requests cancellation of a running task.
    /// In-flight subtasks receive a cancel signal; the task transitions to <see cref="TaskState.Cancelled"/>.
    /// </summary>
    Task CancelAsync(string taskId, CancellationToken ct = default);

    /// <summary>
    /// Returns the current status of a task, or <c>null</c> if not found.
    /// </summary>
    Task<NopTaskRecord?> GetStatusAsync(string taskId, CancellationToken ct = default);
}
