// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using NPS.NOP.Models;

namespace NPS.NOP.Orchestration;

/// <summary>
/// Aggregates results from multiple completed subtasks using the strategy
/// defined in <see cref="SyncFrame.Aggregate"/> or the orchestrator default (NPS-5 §3.3.2).
/// </summary>
public static class NopResultAggregator
{
    private static readonly JsonSerializerOptions s_opts = new() { WriteIndented = false };

    /// <summary>
    /// Aggregates <paramref name="results"/> using <paramref name="strategy"/>.
    /// </summary>
    /// <param name="strategy">One of <see cref="AggregateStrategy"/> constants.</param>
    /// <param name="results">Ordered list of successful subtask results.</param>
    /// <param name="minRequired">
    /// For <see cref="AggregateStrategy.FastestK"/>: how many results to include.
    /// Ignored for other strategies.
    /// </param>
    public static JsonElement Aggregate(
        string strategy,
        IReadOnlyList<JsonElement> results,
        int minRequired = 0)
    {
        if (results.Count == 0)
            return JsonDocument.Parse("{}").RootElement;

        return strategy switch
        {
            AggregateStrategy.First    => results[0],
            AggregateStrategy.All      => BuildArray(results),
            AggregateStrategy.FastestK => BuildArray(results.Take(minRequired > 0 ? minRequired : results.Count).ToList()),
            _                          => Merge(results), // "merge" and default
        };
    }

    // ── Strategies ────────────────────────────────────────────────────────────

    /// <summary>
    /// Merges all JSON object results into one (last-write-wins on key conflicts).
    /// Non-object results are added under <c>"_result_{i}"</c> keys.
    /// </summary>
    public static JsonElement Merge(IReadOnlyList<JsonElement> results)
    {
        var merged = new JsonObject();
        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in result.EnumerateObject())
                    merged[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
            else
            {
                merged[$"_result_{i}"] = JsonNode.Parse(result.GetRawText());
            }
        }
        return JsonDocument.Parse(merged.ToJsonString(s_opts)).RootElement;
    }

    /// <summary>Returns all results as a JSON array.</summary>
    public static JsonElement BuildArray(IReadOnlyList<JsonElement> results)
    {
        var arr = new JsonArray();
        foreach (var r in results)
            arr.Add(JsonNode.Parse(r.GetRawText()));
        return JsonDocument.Parse(arr.ToJsonString(s_opts)).RootElement;
    }

    /// <summary>
    /// Filters <paramref name="allResults"/> to only "end" nodes
    /// (nodes with no outgoing edges), then aggregates.
    /// </summary>
    public static JsonElement AggregateEndNodes(
        IReadOnlyList<string> endNodeIds,
        IReadOnlyDictionary<string, JsonElement> allResults,
        string strategy = AggregateStrategy.Merge)
    {
        var endResults = endNodeIds
            .Where(allResults.ContainsKey)
            .Select(id => allResults[id])
            .ToList();

        return Aggregate(strategy, endResults);
    }
}
