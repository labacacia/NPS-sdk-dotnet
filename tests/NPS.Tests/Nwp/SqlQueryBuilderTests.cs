// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Dapper;
using NPS.NWP.Frames;
using NPS.NWP.MemoryNode;
using NPS.NWP.MemoryNode.Query;

namespace NPS.Tests.Nwp;

public sealed class SqlQueryBuilderTests
{
    private static readonly MemoryNodeSchema Schema = new()
    {
        TableName  = "products",
        PrimaryKey = "id",
        Fields =
        [
            new MemoryNodeField { Name = "id",    Type = "number" },
            new MemoryNodeField { Name = "name",  Type = "string" },
            new MemoryNodeField { Name = "price", Type = "number" },
            new MemoryNodeField { Name = "sku",   Type = "string",
                ColumnName = "product_sku" },
        ],
    };

    private static readonly MemoryNodeOptions Options = new()
    {
        NodeId       = "urn:nps:node:test:products",
        Schema       = Schema,
        PathPrefix   = "/products",
        DefaultLimit = 20,
        MaxLimit     = 100,
    };

    private SqlQueryBuilder MakePg()  => new(Schema, DatabaseDialect.PostgreSql);
    private SqlQueryBuilder MakeSql() => new(Schema, DatabaseDialect.SqlServer);

    // ── SELECT list ───────────────────────────────────────────────────────────

    [Fact]
    public void Build_NoFields_SelectsAllSchemaColumns()
    {
        var frame = new QueryFrame();
        var (sql, _) = MakePg().Build(frame, Options);
        Assert.Contains("\"id\"", sql);
        Assert.Contains("\"name\"", sql);
        Assert.Contains("\"price\"", sql);
        Assert.Contains("\"product_sku\"", sql);
    }

    [Fact]
    public void Build_ColumnAlias_AppliesAlias()
    {
        var frame = new QueryFrame { Fields = ["sku"] };
        var (sql, _) = MakePg().Build(frame, Options);
        // column is product_sku aliased back to sku
        Assert.Contains("\"product_sku\" AS \"sku\"", sql);
    }

    [Fact]
    public void Build_UnknownField_Throws()
    {
        var frame = new QueryFrame { Fields = ["ghost"] };
        Assert.Throws<NwpFilterException>(() => MakePg().Build(frame, Options));
    }

    // ── FROM ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_Pg_QuotesTableName()
    {
        var frame = new QueryFrame();
        var (sql, _) = MakePg().Build(frame, Options);
        Assert.Contains("FROM \"products\"", sql);
    }

    [Fact]
    public void Build_SqlServer_BracketsTableName()
    {
        var frame = new QueryFrame();
        var (sql, _) = MakeSql().Build(frame, Options);
        Assert.Contains("FROM [products]", sql);
    }

    // ── WHERE ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_WithFilter_AppendsWhere()
    {
        var frame = new QueryFrame
        {
            Filter = JsonDocument.Parse("""{"name":{"$eq":"widget"}}""").RootElement,
        };
        var (sql, p) = MakePg().Build(frame, Options);
        Assert.Contains("WHERE", sql);
        Assert.Equal("widget", p.Get<string>("p0"));
    }

    [Fact]
    public void Build_NoFilter_NoWhere()
    {
        var frame = new QueryFrame();
        var (sql, _) = MakePg().Build(frame, Options);
        Assert.DoesNotContain("WHERE", sql);
    }

    // ── ORDER BY ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_NoOrder_DefaultsToPrimaryKey()
    {
        var frame = new QueryFrame();
        var (sql, _) = MakePg().Build(frame, Options);
        Assert.Contains("ORDER BY \"id\"", sql);
    }

    [Fact]
    public void Build_ExplicitOrder_AppliesIt()
    {
        var frame = new QueryFrame
        {
            Order = [new QueryOrderClause("price", "DESC")],
        };
        var (sql, _) = MakePg().Build(frame, Options);
        Assert.Contains("ORDER BY \"price\" DESC", sql);
    }

    [Fact]
    public void Build_UnknownOrderField_Throws()
    {
        var frame = new QueryFrame { Order = [new QueryOrderClause("ghost", "ASC")] };
        Assert.Throws<NwpFilterException>(() => MakePg().Build(frame, Options));
    }

    // ── Pagination ────────────────────────────────────────────────────────────

    [Fact]
    public void Build_Pg_UsesLimitOffset()
    {
        var frame = new QueryFrame { Limit = 10 };
        var (sql, p) = MakePg().Build(frame, Options);
        Assert.Contains("LIMIT @_limit OFFSET @_offset", sql);
        Assert.Equal(10, p.Get<int>("_limit"));
        Assert.Equal(0,  p.Get<int>("_offset"));
    }

    [Fact]
    public void Build_SqlServer_UsesOffsetFetch()
    {
        var frame = new QueryFrame { Limit = 5 };
        var (sql, p) = MakeSql().Build(frame, Options);
        Assert.Contains("OFFSET @_offset ROWS FETCH NEXT @_limit ROWS ONLY", sql);
        Assert.Equal(5, p.Get<int>("_limit"));
    }

    [Fact]
    public void Build_LimitClamped_ToMaxLimit()
    {
        var frame = new QueryFrame { Limit = 999 };
        var (_, p) = MakePg().Build(frame, Options);
        Assert.Equal(100, p.Get<int>("_limit"));  // Options.MaxLimit = 100
    }

    [Fact]
    public void Build_ZeroLimit_UsesDefault()
    {
        var frame = new QueryFrame { Limit = 0 };
        var (_, p) = MakePg().Build(frame, Options);
        Assert.Equal(20, p.Get<int>("_limit"));  // Options.DefaultLimit = 20
    }

    [Fact]
    public void Build_WithCursor_DecodesOffset()
    {
        var cursor = SqlQueryBuilder.EncodeCursor(40)!;
        var frame  = new QueryFrame { Limit = 10, Cursor = cursor };
        var (_, p) = MakePg().Build(frame, Options);
        Assert.Equal(40, p.Get<int>("_offset"));
    }

    // ── Cursor encode/decode ──────────────────────────────────────────────────

    [Fact]
    public void EncodeCursor_ZeroOrNegative_ReturnsNull()
    {
        Assert.Null(SqlQueryBuilder.EncodeCursor(0));
        Assert.Null(SqlQueryBuilder.EncodeCursor(-1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(1000)]
    [InlineData(long.MaxValue / 2)]
    public void CursorRoundtrip(long offset)
    {
        var cursor = SqlQueryBuilder.EncodeCursor(offset);
        Assert.NotNull(cursor);
        Assert.Equal(offset, SqlQueryBuilder.DecodeCursor(cursor));
    }

    [Fact]
    public void DecodeCursor_NullOrEmpty_ReturnsZero()
    {
        Assert.Equal(0L, SqlQueryBuilder.DecodeCursor(null));
        Assert.Equal(0L, SqlQueryBuilder.DecodeCursor(""));
    }

    [Fact]
    public void DecodeCursor_Garbage_ReturnsZero() =>
        Assert.Equal(0L, SqlQueryBuilder.DecodeCursor("not-a-cursor!@#$"));

    // ── BuildCount ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildCount_NoFilter_NoWhere()
    {
        var frame = new QueryFrame();
        var (sql, _) = MakePg().BuildCount(frame);
        Assert.StartsWith("SELECT COUNT(*) FROM", sql);
        Assert.DoesNotContain("WHERE", sql);
    }

    [Fact]
    public void BuildCount_WithFilter_AppendsWhere()
    {
        var frame = new QueryFrame
        {
            Filter = JsonDocument.Parse("""{"price":{"$gt":0}}""").RootElement,
        };
        var (sql, _) = MakePg().BuildCount(frame);
        Assert.Contains("WHERE", sql);
    }
}
