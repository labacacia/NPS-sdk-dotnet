// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Frames;

namespace NPS.NWP.Frames;

// ── QueryFrame (0x10) ────────────────────────────────────────────────────────

/// <summary>
/// Structured data query frame, targeting a Memory Node (NPS-2 §5).
/// Sent to the <c>/query</c> or <c>/stream</c> sub-path of a <c>nwp://</c> address.
/// </summary>
public sealed record QueryFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Query;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>
    /// anchor_id of the schema describing the expected response records.
    /// Conditionally required: the peer must have received the corresponding
    /// <c>AnchorFrame</c> earlier in the session (NPS-1 §5).
    /// </summary>
    [JsonPropertyName("anchor_ref")]
    public string? AnchorRef { get; init; }

    /// <summary>
    /// Filter predicate using the NWP Filter DSL (NPS-2 §5.2).
    /// Supports <c>$eq</c>, <c>$ne</c>, <c>$lt</c>, <c>$lte</c>, <c>$gt</c>, <c>$gte</c>,
    /// <c>$in</c>, <c>$nin</c>, <c>$contains</c>, <c>$between</c>, <c>$and</c>, <c>$or</c>.
    /// <c>null</c> means no filter (return all records up to <see cref="Limit"/>).
    /// </summary>
    public JsonElement? Filter { get; init; }

    /// <summary>
    /// Subset of fields to return. <c>null</c> means return all fields.
    /// Referencing an unknown field MUST result in <c>NWP-QUERY-FIELD-UNKNOWN</c>.
    /// </summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>Maximum records to return. Defaults to 20; max 1000 (NPS-2 §5.1).</summary>
    public uint Limit { get; init; } = 20;

    /// <summary>Opaque pagination cursor from a previous response's <c>next_cursor</c>.</summary>
    public string? Cursor { get; init; }

    /// <summary>Ordering rules. Applied in array order (NPS-2 §5.3).</summary>
    public IReadOnlyList<QueryOrderClause>? Order { get; init; }

    /// <summary>Vector similarity search parameters (NPS-2 §5.4). Requires capability <c>vector_search</c>.</summary>
    [JsonPropertyName("vector_search")]
    public VectorSearchOptions? VectorSearch { get; init; }
}

/// <summary>
/// A single ordering rule within a <see cref="QueryFrame"/> (NPS-2 §5.3).
/// </summary>
/// <param name="Field">Field name to sort by.</param>
/// <param name="Dir">Sort direction: <c>"ASC"</c> or <c>"DESC"</c>.</param>
public sealed record QueryOrderClause(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("dir")]   string Dir);

/// <summary>
/// Vector similarity search parameters within a <see cref="QueryFrame"/> (NPS-2 §5.4).
/// </summary>
public sealed record VectorSearchOptions
{
    /// <summary>Name of the vector field in the schema.</summary>
    public required string Field { get; init; }

    /// <summary>Query vector. Dimensionality MUST match the field definition.</summary>
    public required float[] Vector { get; init; }

    /// <summary>Maximum number of nearest neighbours to return.</summary>
    [JsonPropertyName("top_k")]
    public uint TopK { get; init; } = 10;

    /// <summary>Minimum similarity score to include in results (0.0–1.0).</summary>
    public double? Threshold { get; init; }

    /// <summary>Distance metric: <c>"cosine"</c> (default), <c>"euclidean"</c>, <c>"dot"</c>.</summary>
    public string Metric { get; init; } = "cosine";
}

// ── ActionFrame (0x11) ───────────────────────────────────────────────────────

/// <summary>
/// Operation invocation frame, targeting an Action or Complex Node (NPS-2 §6).
/// Sent to the <c>/invoke</c> sub-path of a <c>nwp://</c> address.
/// </summary>
public sealed record ActionFrame : IFrame
{
    public FrameType    FrameType     => FrameType.Action;
    public EncodingTier PreferredTier => EncodingTier.MsgPack;

    /// <summary>
    /// Operation identifier in <c>{domain}.{verb}</c> format, e.g. <c>"orders.create"</c>.
    /// Unknown action_id MUST result in <c>NWP-ACTION-NOT-FOUND</c>.
    /// </summary>
    [JsonPropertyName("action_id")]
    public required string ActionId { get; init; }

    /// <summary>
    /// Operation parameters. Schema is declared in the node's NWM.
    /// <c>null</c> for actions with no input parameters.
    /// </summary>
    public JsonElement? Params { get; init; }

    /// <summary>
    /// UUID v4 idempotency key. Repeated requests with the same key within 24 hours
    /// MUST return the original result without re-executing the action (NPS-2 §6.1).
    /// </summary>
    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Execution timeout in milliseconds. Default 5000; max 300000 (NPS-2 §6.1).
    /// </summary>
    [JsonPropertyName("timeout_ms")]
    public uint TimeoutMs { get; init; } = 5000;

    /// <summary>
    /// When <c>true</c>, the action executes asynchronously. The response contains
    /// a <c>task_id</c> and <c>poll_url</c> rather than the direct result (NPS-2 §6.2).
    /// </summary>
    [JsonPropertyName("async")]
    public bool Async { get; init; }

    /// <summary>
    /// Optional HTTPS callback URL invoked when an async task completes (NPS-2 §7.1).
    /// MUST be <c>https://</c>. Nodes SHOULD reject private/loopback addresses to avoid SSRF.
    /// </summary>
    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; init; }

    /// <summary>
    /// Task priority hint: <c>"low"</c> / <c>"normal"</c> (default) / <c>"high"</c> (NPS-2 §7.1).
    /// Nodes MAY use it for scheduling or reject unknown values with <c>NWP-ACTION-PARAMS-INVALID</c>.
    /// </summary>
    public string? Priority { get; init; }

    /// <summary>
    /// UUID v4 echoed back in the response and task status for client-side tracing (NPS-2 §7.1).
    /// </summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }
}

/// <summary>
/// Response body for an asynchronous <see cref="ActionFrame"/> execution (NPS-2 §6.2).
/// Returned when <c>ActionFrame.Async == true</c>.
/// </summary>
public sealed record AsyncActionResponse
{
    /// <summary>Unique identifier for the background task.</summary>
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    /// <summary>Current task state: <c>"pending"</c>, <c>"running"</c>, <c>"completed"</c>, <c>"failed"</c>, <c>"cancelled"</c>.</summary>
    public required string Status { get; init; }

    /// <summary>NWP address to poll for task status updates.</summary>
    [JsonPropertyName("poll_url")]
    public required string PollUrl { get; init; }

    /// <summary>Optional estimated execution time in milliseconds.</summary>
    [JsonPropertyName("estimated_ms")]
    public uint? EstimatedMs { get; init; }

    /// <summary>Echo of <see cref="ActionFrame.RequestId"/> for tracing.</summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }
}

/// <summary>
/// Full async task status returned by <c>system.task.status</c> (NPS-2 §7.3).
/// </summary>
public sealed record ActionTaskStatus
{
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    /// <summary><c>"pending"</c> / <c>"running"</c> / <c>"completed"</c> / <c>"failed"</c> / <c>"cancelled"</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Progress 0.0–1.0 when known.</summary>
    public double? Progress { get; init; }

    [JsonPropertyName("created_at")]
    public required string CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public required string UpdatedAt { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    /// <summary>Result payload on <c>"completed"</c>; <c>null</c> otherwise.</summary>
    public JsonElement? Result { get; init; }

    /// <summary>Error payload on <c>"failed"</c>; <c>null</c> otherwise.</summary>
    public JsonElement? Error { get; init; }
}
