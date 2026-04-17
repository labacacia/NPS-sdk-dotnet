// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Frames;
using NPS.NOP.Models;

namespace NPS.NOP.Frames;

// ── TaskFrame (0x40) ────────────────────────────────────────────────────────

/// <summary>
/// Task definition frame. Submitted by the Orchestrator to define an overall
/// task as a DAG of sub-tasks (NPS-5 §3.1).
/// </summary>
public sealed record TaskFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Task;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Task unique identifier (UUID v4).</summary>
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    /// <summary>DAG definition describing sub-tasks and their execution order (NPS-5 §3.1.1).</summary>
    [JsonPropertyName("dag")]
    public required TaskDag Dag { get; init; }

    /// <summary>Overall task timeout in milliseconds. Default 30000, max 3600000 (1 hour).</summary>
    [JsonPropertyName("timeout_ms")]
    public uint TimeoutMs { get; init; } = 30000;

    /// <summary>Global max retries per node before the task fails. Default 2.</summary>
    [JsonPropertyName("max_retries")]
    public byte MaxRetries { get; init; } = 2;

    /// <summary>Task priority: <c>"low"</c>, <c>"normal"</c> (default), or <c>"high"</c>.</summary>
    [JsonPropertyName("priority")]
    public string Priority { get; init; } = TaskPriority.Normal;

    /// <summary>HTTPS callback URL for completion/failure notification (NPS-5 §8.4).</summary>
    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; init; }

    /// <summary>When true, run resource pre-flight checks before execution (NPS-5 §4).</summary>
    [JsonPropertyName("preflight")]
    public bool Preflight { get; init; }

    /// <summary>Transparent context propagated to all sub-tasks (NPS-5 §3.1.2).</summary>
    [JsonPropertyName("context")]
    public TaskContext? Context { get; init; }

    /// <summary>Request tracking ID (UUID v4).</summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    /// <summary>
    /// Current delegation chain depth (0 = root task submitted directly by a client).
    /// The orchestrator rejects tasks whose depth ≥ <see cref="NopConstants.MaxDelegateChainDepth"/>.
    /// Set automatically by the orchestrator when it creates a sub-TaskFrame for a Worker.
    /// </summary>
    [JsonPropertyName("delegate_depth")]
    public int DelegateDepth { get; init; }
}

// ── DelegateFrame (0x41) ────────────────────────────────────────────────────

/// <summary>
/// Sub-task delegation frame. Sent by the Orchestrator to assign a single
/// DAG node to a Worker Agent (NPS-5 §3.2).
/// </summary>
public sealed record DelegateFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Delegate;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Parent task ID.</summary>
    [JsonPropertyName("parent_task_id")]
    public required string ParentTaskId { get; init; }

    /// <summary>Sub-task unique identifier (UUID v4).</summary>
    [JsonPropertyName("subtask_id")]
    public required string SubtaskId { get; init; }

    /// <summary>Corresponding DAG node ID.</summary>
    [JsonPropertyName("node_id")]
    public required string NodeId { get; init; }

    /// <summary>Target Worker Agent NID.</summary>
    [JsonPropertyName("target_agent_nid")]
    public required string TargetAgentNid { get; init; }

    /// <summary>Operation URL (<c>nwp://</c>) or special action (<c>"preflight"</c>, <c>"cancel"</c>).</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>Operation parameters (post input_mapping resolution).</summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

    /// <summary>Scope subset carved from the parent. MUST NOT exceed parent scope (NPS-5 §3.2).</summary>
    [JsonPropertyName("delegated_scope")]
    public required JsonElement DelegatedScope { get; init; }

    /// <summary>Sub-task deadline (ISO 8601 UTC).</summary>
    [JsonPropertyName("deadline_at")]
    public required string DeadlineAt { get; init; }

    /// <summary>Idempotency key for safe retries.</summary>
    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }

    /// <summary>Inherited from TaskFrame.Priority.</summary>
    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    /// <summary>Transparent context (inherited from TaskFrame, span_id updated per delegation).</summary>
    [JsonPropertyName("context")]
    public TaskContext? Context { get; init; }

    /// <summary>
    /// Delegation chain depth at which this frame is dispatched (1 = first-level delegation).
    /// The receiving Worker MUST NOT accept further sub-delegation when depth ≥
    /// <see cref="NopConstants.MaxDelegateChainDepth"/>.
    /// </summary>
    [JsonPropertyName("delegate_depth")]
    public int DelegateDepth { get; init; }
}

// ── SyncFrame (0x42) ────────────────────────────────────────────────────────

/// <summary>
/// Synchronisation barrier frame. Blocks execution until dependent sub-tasks
/// complete, with optional K-of-N semantics (NPS-5 §3.3).
/// </summary>
public sealed record SyncFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Sync;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Parent task ID.</summary>
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    /// <summary>Sync point unique identifier (UUID v4).</summary>
    [JsonPropertyName("sync_id")]
    public required string SyncId { get; init; }

    /// <summary>Sub-task IDs to wait for.</summary>
    [JsonPropertyName("wait_for")]
    public required IReadOnlyList<string> WaitFor { get; init; }

    /// <summary>
    /// Minimum successful sub-tasks required to proceed (K-of-N).
    /// 0 or omitted means all must succeed (NPS-5 §3.3.1).
    /// </summary>
    [JsonPropertyName("min_required")]
    public uint MinRequired { get; init; }

    /// <summary>Result aggregation strategy (NPS-5 §3.3.2). Default <c>"merge"</c>.</summary>
    [JsonPropertyName("aggregate")]
    public string Aggregate { get; init; } = AggregateStrategy.Merge;

    /// <summary>Wait timeout in milliseconds. Exceeding returns <c>NOP-SYNC-TIMEOUT</c>.</summary>
    [JsonPropertyName("timeout_ms")]
    public uint? TimeoutMs { get; init; }
}

// ── AlignStreamFrame (0x43) ─────────────────────────────────────────────────

/// <summary>
/// Directed task stream frame. Replaces the deprecated NCP AlignFrame (0x05).
/// Carries intermediate and final results with DAG context and NIP identity
/// binding (NPS-5 §3.4).
/// </summary>
public sealed record AlignStreamFrame : IFrame
{
    public FrameType    FrameType     => FrameType.AlignStream;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>Stream unique identifier (UUID v4).</summary>
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    /// <summary>Associated parent task ID.</summary>
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    /// <summary>Associated sub-task ID.</summary>
    [JsonPropertyName("subtask_id")]
    public required string SubtaskId { get; init; }

    /// <summary>Strictly increasing message sequence number (0-based).</summary>
    [JsonPropertyName("seq")]
    public required ulong Seq { get; init; }

    /// <summary>CapsFrame anchor reference for intermediate results.</summary>
    [JsonPropertyName("payload_ref")]
    public string? PayloadRef { get; init; }

    /// <summary>Intermediate result data.</summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    /// <summary>Back-pressure window size in NPT tokens (NPS-5 §3.4.1).</summary>
    [JsonPropertyName("window_size")]
    public uint? WindowSize { get; init; }

    /// <summary>True when this is the final frame in the stream.</summary>
    [JsonPropertyName("is_final")]
    public required bool IsFinal { get; init; }

    /// <summary>Sender NID. Receiver MUST verify against connection identity.</summary>
    [JsonPropertyName("sender_nid")]
    public required string SenderNid { get; init; }

    /// <summary>Error details when <see cref="IsFinal"/> is true and the sub-task failed.</summary>
    [JsonPropertyName("error")]
    public StreamError? Error { get; init; }
}
