// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Registry;
using NPS.NWP.Frames;

namespace NPS.Tests.Nwp;

/// <summary>
/// Encode/Decode round-trip tests for NWP frame types (QueryFrame, ActionFrame).
/// </summary>
public sealed class NwpFrameCodecTests
{
    private static FrameRegistry BuildNwpRegistry()
    {
#pragma warning disable CS0618
        return new FrameRegistryBuilder()
            .Register<AnchorFrame>(FrameType.Anchor)
            .Register<DiffFrame>  (FrameType.Diff)
            .Register<StreamFrame>(FrameType.Stream)
            .Register<CapsFrame>  (FrameType.Caps)
            .Register<ErrorFrame> (FrameType.Error)
            .Register<QueryFrame> (FrameType.Query)
            .Register<ActionFrame>(FrameType.Action)
            .Build();
#pragma warning restore CS0618
    }

    private static NpsFrameCodec MakeCodec() =>
        new(new Tier1JsonCodec(), new Tier2MsgPackCodec(), BuildNwpRegistry());

    // ── QueryFrame (0x10) ────────────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void QueryFrame_MinimalRoundTrip(EncodingTier tier)
    {
        var frame = new QueryFrame
        {
            AnchorRef = "sha256:" + new string('1', 64),
            Limit     = 50,
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (QueryFrame)codec.Decode(wire);

        Assert.Equal(frame.AnchorRef, result.AnchorRef);
        Assert.Equal(50u,             result.Limit);
        Assert.Null(result.Filter);
        Assert.Null(result.Fields);
        Assert.Null(result.Cursor);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void QueryFrame_WithFilter_RoundTrip(EncodingTier tier)
    {
        var frame = new QueryFrame
        {
            Filter = JsonDocument.Parse("{\"price\":{\"$gt\":100}}").RootElement,
            Fields = ["id", "name", "price"],
            Limit  = 20,
            Cursor = "cursor_xyz",
            Order  = [new QueryOrderClause("price", "DESC")],
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (QueryFrame)codec.Decode(wire);

        Assert.Equal(frame.Limit,  result.Limit);
        Assert.Equal(frame.Cursor, result.Cursor);
        Assert.Equal(3,            result.Fields!.Count);
        Assert.Equal("id",         result.Fields[0]);
        Assert.NotNull(result.Filter);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void QueryFrame_WithOrderClauses_RoundTrip(EncodingTier tier)
    {
        var frame = new QueryFrame
        {
            Order = [new QueryOrderClause("name", "ASC"), new QueryOrderClause("id", "DESC")],
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (QueryFrame)codec.Decode(wire);

        Assert.Equal(2,     result.Order!.Count);
        Assert.Equal("name", result.Order[0].Field);
        Assert.Equal("ASC",  result.Order[0].Dir);
        Assert.Equal("id",   result.Order[1].Field);
        Assert.Equal("DESC", result.Order[1].Dir);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void QueryFrame_WithVectorSearch_RoundTrip(EncodingTier tier)
    {
        var frame = new QueryFrame
        {
            VectorSearch = new VectorSearchOptions
            {
                Field     = "embedding",
                Vector    = [0.1f, 0.2f, 0.3f],
                TopK      = 5,
                Threshold = 0.85,
                Metric    = "cosine",
            },
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (QueryFrame)codec.Decode(wire);

        Assert.NotNull(result.VectorSearch);
        Assert.Equal("embedding", result.VectorSearch.Field);
        Assert.Equal(5u,          result.VectorSearch.TopK);
        Assert.Equal(0.85,        result.VectorSearch.Threshold);
    }

    [Fact]
    public void QueryFrame_WireHeader_HasCorrectFrameType()
    {
        var frame = new QueryFrame();
        var wire  = MakeCodec().Encode(frame, EncodingTier.Json);
        Assert.Equal((byte)FrameType.Query, wire[0]);
    }

    // ── ActionFrame (0x11) ───────────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void ActionFrame_MinimalRoundTrip(EncodingTier tier)
    {
        var frame = new ActionFrame
        {
            ActionId = "orders.create",
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (ActionFrame)codec.Decode(wire);

        Assert.Equal(frame.ActionId, result.ActionId);
        Assert.Null(result.Params);
        Assert.Null(result.IdempotencyKey);
        Assert.False(result.Async);
        Assert.Equal(5000u, result.TimeoutMs);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void ActionFrame_FullRoundTrip(EncodingTier tier)
    {
        var frame = new ActionFrame
        {
            ActionId        = "inventory.restock",
            Params          = JsonDocument.Parse("{\"sku\":\"ABC\",\"qty\":100}").RootElement,
            IdempotencyKey  = "idem-key-001",
            TimeoutMs       = 30_000,
            Async           = true,
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (ActionFrame)codec.Decode(wire);

        Assert.Equal(frame.ActionId,       result.ActionId);
        Assert.Equal(frame.IdempotencyKey, result.IdempotencyKey);
        Assert.Equal(30_000u,              result.TimeoutMs);
        Assert.True(result.Async);
        Assert.NotNull(result.Params);
    }

    [Fact]
    public void ActionFrame_WireHeader_HasCorrectFrameType()
    {
        var frame = new ActionFrame { ActionId = "test.action" };
        var wire  = MakeCodec().Encode(frame, EncodingTier.Json);
        Assert.Equal((byte)FrameType.Action, wire[0]);
    }

    // ── Mixed registry: NCP frames still decode correctly ────────────────────

    [Fact]
    public void NwpCodec_CanDecodeNcpErrorFrame()
    {
        var frame = new ErrorFrame
        {
            Status  = NpsStatusCodes.ClientBadParam,
            Error   = "NWP-QUERY-FILTER-INVALID",
            Message = "Invalid filter predicate.",
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, EncodingTier.Json);
        var result = (ErrorFrame)codec.Decode(wire);

        Assert.Equal(frame.Status, result.Status);
        Assert.Equal(frame.Error,  result.Error);
    }
}
