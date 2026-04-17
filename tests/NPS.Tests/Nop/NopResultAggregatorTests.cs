// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;

namespace NPS.Tests.Nop;

public class NopResultAggregatorTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    // ── Merge ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_TwoObjects_CombinesFields()
    {
        var results = new[] { J("""{"a": 1}"""), J("""{"b": 2}""") };
        var merged  = NopResultAggregator.Merge(results);
        Assert.Equal(1, merged.GetProperty("a").GetInt32());
        Assert.Equal(2, merged.GetProperty("b").GetInt32());
    }

    [Fact]
    public void Merge_KeyConflict_LastWriteWins()
    {
        var results = new[] { J("""{"x": 1}"""), J("""{"x": 99}""") };
        var merged  = NopResultAggregator.Merge(results);
        Assert.Equal(99, merged.GetProperty("x").GetInt32());
    }

    [Fact]
    public void Merge_NonObjectResult_WrappedWithKey()
    {
        var results = new[] { J("""{"a": 1}"""), J("42") };
        var merged  = NopResultAggregator.Merge(results);
        Assert.True(merged.TryGetProperty("a", out _));
        Assert.True(merged.TryGetProperty("_result_1", out var wrapped));
        Assert.Equal(42, wrapped.GetInt32());
    }

    [Fact]
    public void Merge_EmptyList_ReturnsEmptyObject()
    {
        var result = NopResultAggregator.Merge([]);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }

    // ── First ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_First_ReturnsFirstElement()
    {
        var results = new[] { J("""{"v": 1}"""), J("""{"v": 2}""") };
        var agg = NopResultAggregator.Aggregate(AggregateStrategy.First, results);
        Assert.Equal(1, agg.GetProperty("v").GetInt32());
    }

    // ── All ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_All_ReturnsArray()
    {
        var results = new[] { J("""{"v": 1}"""), J("""{"v": 2}""") };
        var agg = NopResultAggregator.Aggregate(AggregateStrategy.All, results);
        Assert.Equal(JsonValueKind.Array, agg.ValueKind);
        Assert.Equal(2, agg.GetArrayLength());
        Assert.Equal(1, agg[0].GetProperty("v").GetInt32());
        Assert.Equal(2, agg[1].GetProperty("v").GetInt32());
    }

    // ── FastestK ──────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_FastestK_TakesMinRequired()
    {
        var results = new[] { J("""{"v": 1}"""), J("""{"v": 2}"""), J("""{"v": 3}""") };
        var agg = NopResultAggregator.Aggregate(AggregateStrategy.FastestK, results, minRequired: 2);
        Assert.Equal(JsonValueKind.Array, agg.ValueKind);
        Assert.Equal(2, agg.GetArrayLength());
    }

    [Fact]
    public void Aggregate_FastestK_ZeroMinRequired_TakesAll()
    {
        var results = new[] { J("""{"v": 1}"""), J("""{"v": 2}""") };
        var agg = NopResultAggregator.Aggregate(AggregateStrategy.FastestK, results, minRequired: 0);
        Assert.Equal(2, agg.GetArrayLength());
    }

    // ── AggregateEndNodes ─────────────────────────────────────────────────────

    [Fact]
    public void AggregateEndNodes_OnlyIncludesEndNodes()
    {
        var all = new Dictionary<string, JsonElement>
        {
            ["fetch"]   = J("""{"items": [1]}"""),
            ["analyze"] = J("""{"score": 0.9}"""),
            ["report"]  = J("""{"summary": "ok"}"""),
        };
        var endNodeIds = new[] { "report" };
        var result = NopResultAggregator.AggregateEndNodes(endNodeIds, all);
        Assert.True(result.TryGetProperty("summary", out _));
        Assert.False(result.TryGetProperty("score", out _));
    }

    [Fact]
    public void AggregateEndNodes_EmptyResults_ReturnsEmptyObject()
    {
        var result = NopResultAggregator.AggregateEndNodes(["report"], new Dictionary<string, JsonElement>());
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }

    // ── Default strategy ──────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_UnknownStrategy_FallsBackToMerge()
    {
        var results = new[] { J("""{"a": 1}"""), J("""{"b": 2}""") };
        var agg = NopResultAggregator.Aggregate("unknown_strategy", results);
        Assert.True(agg.TryGetProperty("a", out _));
        Assert.True(agg.TryGetProperty("b", out _));
    }
}
