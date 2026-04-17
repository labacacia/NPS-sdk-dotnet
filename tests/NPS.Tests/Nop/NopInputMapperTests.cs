// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NOP.Orchestration;

namespace NPS.Tests.Nop;

public class NopInputMapperTests
{
    private static readonly JsonSerializerOptions s_opts = new() { WriteIndented = false };

    private static IReadOnlyDictionary<string, JsonElement> Context(string nodeId, string json) =>
        new Dictionary<string, JsonElement> { [nodeId] = JsonDocument.Parse(json).RootElement };

    // ── Resolve ───────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_TopLevelNode_ReturnsFullResult()
    {
        var ctx = Context("fetch", """{"count": 3}""");
        var result = NopInputMapper.Resolve("$.fetch", ctx);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Resolve_NestedField_ReturnsValue()
    {
        var ctx = Context("analyze", """{"result": {"score": 0.92}}""");
        var result = NopInputMapper.Resolve("$.analyze.result.score", ctx);
        Assert.NotNull(result);
        Assert.Equal(0.92, result!.Value.GetDouble(), precision: 5);
    }

    [Fact]
    public void Resolve_MissingNode_ReturnsNull()
    {
        var ctx = new Dictionary<string, JsonElement>();
        var result = NopInputMapper.Resolve("$.missing.field", ctx);
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_MissingField_ReturnsNull()
    {
        var ctx = Context("fetch", """{"count": 1}""");
        var result = NopInputMapper.Resolve("$.fetch.no_such_field", ctx);
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_DollarOnly_ReturnsFullContext()
    {
        var ctx = Context("a", """{"x": 1}""");
        var result = NopInputMapper.Resolve("$.", ctx);
        // Should not throw — returns serialized context
        Assert.NotNull(result);
    }

    [Fact]
    public void Resolve_NoPrefix_Throws()
    {
        var ctx = new Dictionary<string, JsonElement>();
        Assert.Throws<NopMappingException>(() => NopInputMapper.Resolve("fetch.field", ctx));
    }

    [Fact]
    public void Resolve_EmptyPath_Throws()
    {
        var ctx = new Dictionary<string, JsonElement>();
        Assert.Throws<NopMappingException>(() => NopInputMapper.Resolve("", ctx));
    }

    [Fact]
    public void Resolve_DepthExceeded_Throws()
    {
        var ctx = Context("n", """{}""");
        var deepPath = "$.n." + string.Join(".", Enumerable.Repeat("a", 10));
        Assert.Throws<NopMappingException>(() => NopInputMapper.Resolve(deepPath, ctx));
    }

    // ── BuildParams ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildParams_NullMapping_ReturnsEmptyObject()
    {
        var ctx    = new Dictionary<string, JsonElement>();
        var result = NopInputMapper.BuildParams(null, ctx);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal(0, result.EnumerateObject().Count());
    }

    [Fact]
    public void BuildParams_StringPath_ResolvesValue()
    {
        var ctx = Context("fetch", """{"items": [1, 2, 3]}""");
        var mapping = new Dictionary<string, JsonElement>
        {
            ["products"] = JsonDocument.Parse("\"$.fetch.items\"").RootElement,
        };
        var result = NopInputMapper.BuildParams(mapping, ctx);
        Assert.True(result.TryGetProperty("products", out var items));
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
    }

    [Fact]
    public void BuildParams_ArrayPaths_BuildsList()
    {
        var ctx = new Dictionary<string, JsonElement>
        {
            ["a"] = JsonDocument.Parse("""{"v": 1}""").RootElement,
            ["b"] = JsonDocument.Parse("""{"v": 2}""").RootElement,
        };
        var mapping = new Dictionary<string, JsonElement>
        {
            ["combined"] = JsonDocument.Parse("""["$.a.v", "$.b.v"]""").RootElement,
        };
        var result = NopInputMapper.BuildParams(mapping, ctx);
        Assert.True(result.TryGetProperty("combined", out var combined));
        Assert.Equal(JsonValueKind.Array, combined.ValueKind);
    }
}
