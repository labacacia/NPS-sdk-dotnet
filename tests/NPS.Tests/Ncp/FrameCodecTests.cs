// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Registry;

namespace NPS.Tests.Ncp;

/// <summary>
/// Encode/Decode round-trip tests for all NCP frame types via both Tier-1 (JSON)
/// and Tier-2 (MsgPack) codecs. Also verifies header construction and EXT mode.
/// </summary>
public sealed class FrameCodecTests
{
    private static readonly FrameRegistry  Registry = FrameRegistry.CreateDefault();
    private static readonly Tier1JsonCodec Json     = new();
    private static readonly Tier2MsgPackCodec MsgPack = new();

    private static NpsFrameCodec MakeCodec(uint maxPayload = FrameHeader.DefaultMaxPayload) =>
        new(Json, MsgPack, Registry, maxPayload);

    private static FrameSchema MakeSchema() => new()
    {
        Fields =
        [
            new SchemaField("id",    "uint64",  "entity.id"),
            new SchemaField("name",  "string",  "entity.label"),
            new SchemaField("price", "decimal", "commerce.price.usd", Nullable: true),
        ]
    };

    // ── AnchorFrame ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void AnchorFrame_RoundTrip(EncodingTier tier)
    {
        var frame = new AnchorFrame
        {
            AnchorId = "sha256:" + new string('a', 64),
            Schema   = MakeSchema(),
            Ttl      = 7200,
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (AnchorFrame)codec.Decode(wire);

        Assert.Equal(frame.AnchorId, result.AnchorId);
        Assert.Equal(frame.Ttl,      result.Ttl);
        Assert.Equal(frame.Schema.Fields.Count, result.Schema.Fields.Count);
        Assert.Equal("id",           result.Schema.Fields[0].Name);
        Assert.Equal("entity.id",    result.Schema.Fields[0].Semantic);
    }

    [Fact]
    public void AnchorFrame_WireHeader_HasCorrectFrameType()
    {
        var frame = new AnchorFrame { AnchorId = "sha256:" + new string('b', 64), Schema = MakeSchema() };
        var wire  = MakeCodec().Encode(frame, EncodingTier.MsgPack);

        Assert.Equal((byte)FrameType.Anchor, wire[0]);
    }

    // ── DiffFrame ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void DiffFrame_RoundTrip(EncodingTier tier)
    {
        var frame = new DiffFrame
        {
            AnchorRef = "sha256:" + new string('c', 64),
            BaseSeq   = 42,
            EntityId  = "product:1001",
            Patch     =
            [
                new JsonPatchOperation("replace", "/price", JsonDocument.Parse("99.99").RootElement),
                new JsonPatchOperation("remove",  "/name"),
            ],
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (DiffFrame)codec.Decode(wire);

        Assert.Equal(frame.AnchorRef, result.AnchorRef);
        Assert.Equal(frame.BaseSeq,   result.BaseSeq);
        Assert.Equal(frame.EntityId,  result.EntityId);
        Assert.Equal(2,               result.Patch.Count);
        Assert.Equal("replace",       result.Patch[0].Op);
        Assert.Equal("/price",        result.Patch[0].Path);
    }

    // ── StreamFrame ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void StreamFrame_RoundTrip(EncodingTier tier)
    {
        var frame = new StreamFrame
        {
            StreamId   = "stream-001",
            Seq        = 3,
            IsLast     = false,
            AnchorRef  = "sha256:" + new string('d', 64),
            Data       = [JsonDocument.Parse("{\"id\":1}").RootElement],
            WindowSize = 10,
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (StreamFrame)codec.Decode(wire);

        Assert.Equal(frame.StreamId,   result.StreamId);
        Assert.Equal(frame.Seq,        result.Seq);
        Assert.False(result.IsLast);
        Assert.Equal(frame.WindowSize, result.WindowSize);
        Assert.Single(result.Data);
    }

    [Fact]
    public void StreamFrame_NotLast_HeaderFinalFlagNotSet()
    {
        var frame = new StreamFrame
        {
            StreamId = "s1", Seq = 0, IsLast = false,
            Data     = [JsonDocument.Parse("{}").RootElement],
        };
        var wire  = MakeCodec().Encode(frame, EncodingTier.Json);
        var flags = (FrameFlags)wire[1];
        Assert.Equal(0, (byte)flags & (byte)FrameFlags.Final);
    }

    [Fact]
    public void StreamFrame_IsLast_HeaderFinalFlagSet()
    {
        var frame = new StreamFrame
        {
            StreamId = "s1", Seq = 1, IsLast = true,
            Data     = [JsonDocument.Parse("{}").RootElement],
        };
        var wire  = MakeCodec().Encode(frame, EncodingTier.Json);
        var flags = (FrameFlags)wire[1];
        Assert.NotEqual(0, (byte)flags & (byte)FrameFlags.Final);
    }

    // ── CapsFrame ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void CapsFrame_RoundTrip(EncodingTier tier)
    {
        var frame = new CapsFrame
        {
            AnchorRef      = "sha256:" + new string('e', 64),
            Count          = 2,
            Data           = [JsonDocument.Parse("{\"id\":1}").RootElement, JsonDocument.Parse("{\"id\":2}").RootElement],
            NextCursor     = "cursor_abc",
            TokenEst       = 512,
            Cached         = false,
            TokenizerUsed  = "cl100k_base",
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (CapsFrame)codec.Decode(wire);

        Assert.Equal(frame.AnchorRef,     result.AnchorRef);
        Assert.Equal(frame.Count,         result.Count);
        Assert.Equal(2,                   result.Data.Count);
        Assert.Equal(frame.NextCursor,    result.NextCursor);
        Assert.Equal(frame.TokenEst,      result.TokenEst);
        Assert.Equal(frame.TokenizerUsed, result.TokenizerUsed);
    }

    // ── ErrorFrame ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void ErrorFrame_RoundTrip(EncodingTier tier)
    {
        var frame = new ErrorFrame
        {
            Status  = NpsStatusCodes.ClientNotFound,
            Error   = "NCP-ANCHOR-NOT-FOUND",
            Message = "Anchor not in cache.",
            Details = JsonDocument.Parse("{\"anchor_ref\":\"sha256:abc\"}").RootElement,
        };

        var codec  = MakeCodec();
        var wire   = codec.Encode(frame, tier);
        var result = (ErrorFrame)codec.Decode(wire);

        Assert.Equal(frame.Status,  result.Status);
        Assert.Equal(frame.Error,   result.Error);
        Assert.Equal(frame.Message, result.Message);
        Assert.Equal((byte)FrameType.Error, wire[0]);
    }

    // ── EXT header mode ──────────────────────────────────────────────────────

    [Fact]
    public void Encode_PayloadExceedsDefaultMax_WithExtEnabled_UsesExtHeader()
    {
        // Build a frame whose JSON payload will exceed 64 KiB.
        // Each element carries a ~100-byte name string; 800 items ≈ 90 KB.
        var longName = new string('x', 100);
        var bigData = Enumerable.Range(0, 800)
            .Select(i => JsonDocument.Parse($"{{\"id\":{i},\"name\":\"{longName}_{i}\"}}").RootElement)
            .ToList();

        var frame = new CapsFrame
        {
            AnchorRef = "sha256:" + new string('f', 64),
            Count     = (uint)bigData.Count,
            Data      = bigData,
        };

        // maxPayload = 4 MB → EXT mode enabled
        var codec = MakeCodec(maxPayload: 4 * 1024 * 1024);
        var wire  = codec.Encode(frame, EncodingTier.Json);

        var flags = (FrameFlags)wire[1];
        // EXT flag must be set (bit 7)
        Assert.NotEqual(0, (byte)flags & (byte)FrameFlags.Ext);
        Assert.Equal(FrameHeader.ExtendedSize, FrameHeader.Parse(wire).HeaderSize);
    }

    [Fact]
    public void Encode_PayloadExceedsMaxPayload_ThrowsNpsCodecException()
    {
        var frame = new AnchorFrame { AnchorId = "sha256:" + new string('g', 64), Schema = MakeSchema() };
        // maxPayload = 1 byte → always too small
        var codec = MakeCodec(maxPayload: 1);
        Assert.Throws<NpsCodecException>(() => codec.Encode(frame, EncodingTier.Json));
    }

    // ── PeekHeader ───────────────────────────────────────────────────────────

    [Fact]
    public void PeekHeader_ReturnsHeaderWithoutDecoding()
    {
        var frame = new AnchorFrame { AnchorId = "sha256:" + new string('h', 64), Schema = MakeSchema() };
        var wire  = MakeCodec().Encode(frame, EncodingTier.MsgPack);
        var peek  = NpsFrameCodec.PeekHeader(wire);

        Assert.Equal(FrameType.Anchor, peek.FrameType);
    }

    // ── Unknown frame type ───────────────────────────────────────────────────

    [Fact]
    public void Decode_UnknownFrameType_ThrowsNpsFrameException()
    {
        // Manufacture a 4-byte header with an unregistered type (0xFF)
        byte[] wire = [0xFF, 0x05, 0x00, 0x01, 0x00]; // 1-byte payload
        Assert.Throws<NpsFrameException>(() => MakeCodec().Decode(wire));
    }
}
