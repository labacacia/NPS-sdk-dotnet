// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NOP.Models;

/// <summary>
/// DAG (Directed Acyclic Graph) definition for a TaskFrame (NPS-5 §3.1.1).
/// </summary>
public sealed record TaskDag
{
    /// <summary>DAG vertices — each represents a sub-task to execute.</summary>
    [JsonPropertyName("nodes")]
    public required IReadOnlyList<DagNode> Nodes { get; init; }

    /// <summary>Directed edges defining execution order and data flow.</summary>
    [JsonPropertyName("edges")]
    public required IReadOnlyList<DagEdge> Edges { get; init; }
}

/// <summary>
/// A single node (vertex) in a task DAG (NPS-5 §3.1.1).
/// </summary>
public sealed record DagNode
{
    /// <summary>Node unique identifier (unique within the DAG).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Operation URL (<c>nwp://...</c>).</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>Worker Agent NID that executes this node.</summary>
    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    /// <summary>Upstream node IDs this node depends on. Empty or null for start nodes.</summary>
    [JsonPropertyName("input_from")]
    public IReadOnlyList<string>? InputFrom { get; init; }

    /// <summary>Upstream output → local input parameter mapping using JSONPath (NPS-5 §3.1.3).</summary>
    [JsonPropertyName("input_mapping")]
    public IReadOnlyDictionary<string, JsonElement>? InputMapping { get; init; }

    /// <summary>Per-node timeout in milliseconds. Overrides TaskFrame.TimeoutMs.</summary>
    [JsonPropertyName("timeout_ms")]
    public uint? TimeoutMs { get; init; }

    /// <summary>Per-node retry strategy (NPS-5 §3.1.4).</summary>
    [JsonPropertyName("retry_policy")]
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>CEL subset condition expression. When false, the node is skipped (NPS-5 §3.1.5).</summary>
    [JsonPropertyName("condition")]
    public string? Condition { get; init; }

    /// <summary>
    /// K-of-N: minimum number of <see cref="InputFrom"/> dependencies that must succeed
    /// before this node is dispatched. 0 or omitted means all deps must succeed (NPS-5 §3.3.1).
    /// </summary>
    [JsonPropertyName("min_required")]
    public uint MinRequired { get; init; }
}

/// <summary>
/// A directed edge in a task DAG (NPS-5 §3.1.1).
/// </summary>
public sealed record DagEdge
{
    /// <summary>Source node ID.</summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>Target node ID.</summary>
    [JsonPropertyName("to")]
    public required string To { get; init; }
}
