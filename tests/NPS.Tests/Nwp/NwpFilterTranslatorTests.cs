// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Dapper;
using NPS.NWP.Http;
using NPS.NWP.MemoryNode;
using NPS.NWP.MemoryNode.Query;

namespace NPS.Tests.Nwp;

public sealed class NwpFilterTranslatorTests
{
    // ── Schema fixture ────────────────────────────────────────────────────────

    private static readonly MemoryNodeSchema Schema = new()
    {
        TableName  = "products",
        PrimaryKey = "id",
        Fields =
        [
            new MemoryNodeField { Name = "id",       Type = "number"  },
            new MemoryNodeField { Name = "name",      Type = "string"  },
            new MemoryNodeField { Name = "price",     Type = "number"  },
            new MemoryNodeField { Name = "active",    Type = "boolean" },
            new MemoryNodeField { Name = "category",  Type = "string"  },
        ],
    };

    private NwpFilterTranslator MakePg()  => new(Schema, DatabaseDialect.PostgreSql);
    private NwpFilterTranslator MakeSql() => new(Schema, DatabaseDialect.SqlServer);

    private static JsonElement? Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    // ── Null / empty filter ───────────────────────────────────────────────────

    [Fact]
    public void NullFilter_ReturnsEmpty()
    {
        var p = new DynamicParameters();
        Assert.Equal(string.Empty, MakePg().Translate(null, p));
    }

    [Fact]
    public void JsonNullFilter_ReturnsEmpty()
    {
        var p = new DynamicParameters();
        Assert.Equal(string.Empty, MakePg().Translate(Parse("null"), p));
    }

    // ── $eq / $ne ─────────────────────────────────────────────────────────────

    [Fact]
    public void Eq_PostgreSql_QuotesCorrectly()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(Parse("""{"name":{"$eq":"widget"}}"""), p);
        Assert.Equal("\"name\" = @p0", sql);
        Assert.Equal("widget", p.Get<string>("p0"));
    }

    [Fact]
    public void Eq_SqlServer_UsesBrackets()
    {
        var p = new DynamicParameters();
        var sql = MakeSql().Translate(Parse("""{"name":{"$eq":"widget"}}"""), p);
        Assert.Equal("[name] = @p0", sql);
    }

    [Fact]
    public void Ne_ProducesNotEquals()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(Parse("""{"price":{"$ne":0}}"""), p);
        Assert.Contains("<>", sql);
        Assert.Equal(0L, p.Get<long>("p0"));
    }

    // ── Comparison operators ──────────────────────────────────────────────────

    [Theory]
    [InlineData("$lt",  "<")]
    [InlineData("$lte", "<=")]
    [InlineData("$gt",  ">")]
    [InlineData("$gte", ">=")]
    public void ComparisonOps_ProduceCorrectSql(string op, string expectedOp)
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(Parse($$$"""{"price":{"{{{op}}}":10}}"""), p);
        Assert.Contains(expectedOp, sql);
    }

    // ── $contains ────────────────────────────────────────────────────────────

    [Fact]
    public void Contains_WrapsWithPercent()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(Parse("""{"name":{"$contains":"wid"}}"""), p);
        Assert.Contains("LIKE", sql);
        Assert.Equal("%wid%", p.Get<string>("p0"));
    }

    // ── $in / $nin ────────────────────────────────────────────────────────────

    [Fact]
    public void In_ProducesInClause()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(Parse("""{"category":{"$in":["A","B"]}}"""), p);
        Assert.Contains("IN @p0", sql);
        var vals = p.Get<IEnumerable<object?>>("p0").ToList();
        Assert.Equal(2, vals.Count);
    }

    [Fact]
    public void In_EmptyArray_ReturnsFalse()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(Parse("""{"category":{"$in":[]}}"""), p);
        Assert.Equal("1=0", sql);
    }

    [Fact]
    public void Nin_EmptyArray_ReturnsTrue()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(Parse("""{"category":{"$nin":[]}}"""), p);
        Assert.Equal("1=1", sql);
    }

    [Fact]
    public void Nin_ProducesNotInClause()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(Parse("""{"category":{"$nin":["X"]}}"""), p);
        Assert.Contains("NOT IN @p0", sql);
    }

    // ── $between ─────────────────────────────────────────────────────────────

    [Fact]
    public void Between_ProducesBetweenClause()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(Parse("""{"price":{"$between":[10,99]}}"""), p);
        Assert.Contains("BETWEEN @p0 AND @p1", sql);
        Assert.Equal(10L, p.Get<long>("p0"));
        Assert.Equal(99L, p.Get<long>("p1"));
    }

    [Fact]
    public void Between_WrongLength_Throws()
    {
        var p = new DynamicParameters();
        Assert.Throws<NwpFilterException>(() =>
            MakePg().Translate(Parse("""{"price":{"$between":[10]}}"""), p));
    }

    // ── $and / $or ────────────────────────────────────────────────────────────

    [Fact]
    public void And_JoinsWithAnd()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(
            Parse("""{"$and":[{"name":{"$eq":"x"}},{"active":{"$eq":true}}]}"""), p);
        Assert.Contains(" AND ", sql);
    }

    [Fact]
    public void Or_JoinsWithOr()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(
            Parse("""{"$or":[{"price":{"$lt":5}},{"price":{"$gt":100}}]}"""), p);
        Assert.Contains(" OR ", sql);
    }

    [Fact]
    public void MultiFieldObject_ImplicitAnd()
    {
        var p = new DynamicParameters();
        var sql = MakePg().Translate(
            Parse("""{"name":{"$eq":"x"},"active":{"$eq":true}}"""), p);
        Assert.Contains("AND", sql);
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void UnknownField_Throws_QueryFieldUnknown()
    {
        var p = new DynamicParameters();
        var ex = Assert.Throws<NwpFilterException>(() =>
            MakePg().Translate(Parse("""{"ghost":{"$eq":1}}"""), p));
        Assert.Equal(NwpErrorCodes.QueryFieldUnknown, ex.NwpErrorCode);
    }

    [Fact]
    public void UnknownOperator_Throws()
    {
        var p = new DynamicParameters();
        Assert.Throws<NwpFilterException>(() =>
            MakePg().Translate(Parse("""{"name":{"$regex":".*"}}"""), p));
    }

    [Fact]
    public void UnknownLogicalOp_Throws()
    {
        var p = new DynamicParameters();
        Assert.Throws<NwpFilterException>(() =>
            MakePg().Translate(Parse("""{"$not":[{"name":{"$eq":"x"}}]}"""), p));
    }

    [Fact]
    public void LogicalOp_NonArray_Throws()
    {
        var p = new DynamicParameters();
        Assert.Throws<NwpFilterException>(() =>
            MakePg().Translate(Parse("""{"$and":{"name":{"$eq":"x"}}}"""), p));
    }
}
