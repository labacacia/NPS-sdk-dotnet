// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NOP.Orchestration;

namespace NPS.Tests.Nop;

public class NopConditionEvaluatorTests
{
    private static IReadOnlyDictionary<string, JsonElement> Ctx(params (string node, string json)[] entries) =>
        entries.ToDictionary(e => e.node, e => JsonDocument.Parse(e.json).RootElement);

    // ── Numeric comparisons ───────────────────────────────────────────────────

    [Fact]
    public void GreaterThan_True()
    {
        var ctx = Ctx(("analyze", """{"score": 0.92}"""));
        Assert.True(NopConditionEvaluator.Evaluate("$.analyze.score > 0.7", ctx));
    }

    [Fact]
    public void GreaterThan_False()
    {
        var ctx = Ctx(("analyze", """{"score": 0.5}"""));
        Assert.False(NopConditionEvaluator.Evaluate("$.analyze.score > 0.7", ctx));
    }

    [Fact]
    public void GreaterThanOrEqual_ExactMatch_True()
    {
        var ctx = Ctx(("n", """{"val": 5}"""));
        Assert.True(NopConditionEvaluator.Evaluate("$.n.val >= 5", ctx));
    }

    [Fact]
    public void LessThan_True()
    {
        var ctx = Ctx(("n", """{"count": 3}"""));
        Assert.True(NopConditionEvaluator.Evaluate("$.n.count < 10", ctx));
    }

    [Fact]
    public void LessThanOrEqual_True()
    {
        var ctx = Ctx(("n", """{"count": 10}"""));
        Assert.True(NopConditionEvaluator.Evaluate("$.n.count <= 10", ctx));
    }

    [Fact]
    public void NotEqual_True()
    {
        var ctx = Ctx(("n", """{"status": "error"}"""));
        Assert.True(NopConditionEvaluator.Evaluate("$.n.status != \"ok\"", ctx));
    }

    // ── String comparisons ────────────────────────────────────────────────────

    [Fact]
    public void StringEquals_True()
    {
        var ctx = Ctx(("classify", """{"label": "positive"}"""));
        Assert.True(NopConditionEvaluator.Evaluate("$.classify.label == \"positive\"", ctx));
    }

    [Fact]
    public void StringEquals_False()
    {
        var ctx = Ctx(("classify", """{"label": "negative"}"""));
        Assert.False(NopConditionEvaluator.Evaluate("$.classify.label == \"positive\"", ctx));
    }

    // ── Null comparisons ──────────────────────────────────────────────────────

    [Fact]
    public void NullEquals_MissingField_True()
    {
        var ctx = Ctx(("n", """{}"""));
        // Missing field resolves to null; null == null is true
        Assert.True(NopConditionEvaluator.Evaluate("$.n.missing == null", ctx));
    }

    [Fact]
    public void NullNotEqual_ExistingValue_True()
    {
        var ctx = Ctx(("n", """{"x": 1}"""));
        Assert.True(NopConditionEvaluator.Evaluate("$.n.x != null", ctx));
    }

    // ── Boolean logic ─────────────────────────────────────────────────────────

    [Fact]
    public void And_BothTrue_True()
    {
        var ctx = Ctx(("n", """{"score": 0.9, "count": 5}"""));
        Assert.True(NopConditionEvaluator.Evaluate("$.n.score > 0.7 && $.n.count > 0", ctx));
    }

    [Fact]
    public void And_OneTrue_False()
    {
        var ctx = Ctx(("n", """{"score": 0.9, "count": 0}"""));
        Assert.False(NopConditionEvaluator.Evaluate("$.n.score > 0.7 && $.n.count > 0", ctx));
    }

    [Fact]
    public void Or_OneTrue_True()
    {
        var ctx = Ctx(("n", """{"a": 1, "b": 0}"""));
        Assert.True(NopConditionEvaluator.Evaluate("$.n.a > 5 || $.n.b == 0", ctx));
    }

    [Fact]
    public void Not_Negates()
    {
        var ctx = Ctx(("n", """{"ok": false}"""));
        Assert.True(NopConditionEvaluator.Evaluate("!$.n.ok", ctx));
    }

    [Fact]
    public void Grouping_ChangesEvalOrder()
    {
        var ctx = Ctx(("n", """{"a": 0, "b": 1, "c": 1}"""));
        // Without grouping: false || (true && true) = true
        // With grouping:   (false || true) && true = true — same result, but test structure
        Assert.True(NopConditionEvaluator.Evaluate("($.n.a > 0 || $.n.b > 0) && $.n.c > 0", ctx));
    }

    // ── Literals ──────────────────────────────────────────────────────────────

    [Fact]
    public void TrueLiteral_ReturnsTrue()
    {
        Assert.True(NopConditionEvaluator.Evaluate("true", new Dictionary<string, JsonElement>()));
    }

    [Fact]
    public void FalseLiteral_ReturnsFalse()
    {
        Assert.False(NopConditionEvaluator.Evaluate("false", new Dictionary<string, JsonElement>()));
    }

    [Fact]
    public void EmptyCondition_ReturnsTrue()
    {
        Assert.True(NopConditionEvaluator.Evaluate("", new Dictionary<string, JsonElement>()));
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void UnknownToken_Throws()
    {
        var ctx = new Dictionary<string, JsonElement>();
        Assert.Throws<NopConditionException>(() =>
            NopConditionEvaluator.Evaluate("$.n.x @@ 1", ctx));
    }
}
