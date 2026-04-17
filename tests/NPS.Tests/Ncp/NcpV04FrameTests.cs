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
/// Tests for NCP v0.4 additions: DiffFrame.PatchFormat/BinaryBitset,
/// CapsFrame.InlineAnchor, NcpErrorCodes, NpsStatusCodes, and v0.4 exceptions.
/// </summary>
public sealed class NcpV04FrameTests
{
    private static readonly FrameRegistry    Registry = FrameRegistry.CreateDefault();
    private static readonly Tier1JsonCodec   Json     = new();
    private static readonly Tier2MsgPackCodec MsgPack = new();
    private static NpsFrameCodec Codec() => new(Json, MsgPack, Registry);

    private static FrameSchema MakeSchema() => new()
    {
        Fields =
        [
            new SchemaField("id",    "uint64",  "entity.id"),
            new SchemaField("price", "decimal", "commerce.price.usd", Nullable: true),
        ]
    };

    // ── DiffFrame.PatchFormat ────────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void DiffFrame_DefaultPatchFormat_IsJsonPatch(EncodingTier tier)
    {
        var frame = new DiffFrame
        {
            AnchorRef = "sha256:" + new string('a', 64),
            BaseSeq   = 1,
            Patch     = [new JsonPatchOperation("replace", "/price", JsonDocument.Parse("9.99").RootElement)],
        };

        var result = (DiffFrame)Codec().Decode(Codec().Encode(frame, tier));
        Assert.Equal(NcpPatchFormat.JsonPatch, result.PatchFormat);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void DiffFrame_ExplicitPatchFormat_RoundTrips(EncodingTier tier)
    {
        var frame = new DiffFrame
        {
            AnchorRef   = "sha256:" + new string('b', 64),
            BaseSeq     = 5,
            PatchFormat = NcpPatchFormat.JsonPatch,
            Patch       = [new JsonPatchOperation("remove", "/stock")],
        };

        var result = (DiffFrame)Codec().Decode(Codec().Encode(frame, tier));
        Assert.Equal(NcpPatchFormat.JsonPatch, result.PatchFormat);
        Assert.Single(result.Patch!);
        Assert.Equal("remove", result.Patch![0].Op);
    }

    // ── DiffFrame.BinaryBitset ───────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void DiffFrame_BinaryBitset_RoundTrips(EncodingTier tier)
    {
        // 2-field schema → bitset is 1 byte; both fields changed = 0b0000_0011
        // Values: id=999 (uint64), price=29.99 (decimal string) — MsgPack encoded
        var rawBitset = new byte[] { 0b0000_0011, 0x00, 0x01 }; // synthetic raw bytes

        var frame = new DiffFrame
        {
            AnchorRef   = "sha256:" + new string('c', 64),
            BaseSeq     = 10,
            PatchFormat = NcpPatchFormat.BinaryBitset,
            BinaryBitset = rawBitset,
        };

        var result = (DiffFrame)Codec().Decode(Codec().Encode(frame, tier));

        Assert.Equal(NcpPatchFormat.BinaryBitset, result.PatchFormat);
        Assert.Equal(rawBitset, result.BinaryBitset);
        Assert.Null(result.Patch); // json_patch Patch is null in binary_bitset mode
    }

    [Fact]
    public void DiffFrame_BothPatchFields_Null_StillEncodes()
    {
        // Edge case: both Patch and BinaryBitset null → should encode without error
        var frame = new DiffFrame
        {
            AnchorRef = "sha256:" + new string('d', 64),
            BaseSeq   = 0,
        };
        var wire = Codec().Encode(frame, EncodingTier.Json);
        var result = (DiffFrame)Codec().Decode(wire);
        Assert.Equal(NcpPatchFormat.JsonPatch, result.PatchFormat); // default
        Assert.Null(result.Patch);
        Assert.Null(result.BinaryBitset);
    }

    // ── CapsFrame.InlineAnchor ───────────────────────────────────────────────

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void CapsFrame_InlineAnchor_RoundTrips(EncodingTier tier)
    {
        var schema = MakeSchema();
        var inline = new AnchorFrame
        {
            AnchorId = "sha256:" + new string('e', 64),
            Schema   = schema,
            Ttl      = 3600,
        };

        var frame = new CapsFrame
        {
            AnchorRef   = "sha256:" + new string('f', 64),
            Count       = 1,
            Data        = [JsonDocument.Parse("{\"id\":1}").RootElement],
            InlineAnchor = inline,
        };

        var result = (CapsFrame)Codec().Decode(Codec().Encode(frame, tier));

        Assert.NotNull(result.InlineAnchor);
        Assert.Equal(inline.AnchorId, result.InlineAnchor.AnchorId);
        Assert.Equal(inline.Schema.Fields.Count, result.InlineAnchor.Schema.Fields.Count);
        Assert.Equal("id", result.InlineAnchor.Schema.Fields[0].Name);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public void CapsFrame_NullInlineAnchor_RoundTrips(EncodingTier tier)
    {
        var frame = new CapsFrame
        {
            AnchorRef = "sha256:" + new string('g', 64),
            Count     = 0,
            Data      = [],
        };

        var result = (CapsFrame)Codec().Decode(Codec().Encode(frame, tier));
        Assert.Null(result.InlineAnchor);
    }

    // ── NcpErrorCodes constants ──────────────────────────────────────────────

    [Fact]
    public void NcpErrorCodes_AllV04CodesPresent()
    {
        Assert.Equal("NCP-ANCHOR-STALE",           NcpErrorCodes.AnchorStale);
        Assert.Equal("NCP-DIFF-FORMAT-UNSUPPORTED", NcpErrorCodes.DiffFormatUnsupported);
        Assert.Equal("NCP-VERSION-INCOMPATIBLE",   NcpErrorCodes.VersionIncompatible);
        Assert.Equal("NCP-STREAM-WINDOW-OVERFLOW", NcpErrorCodes.StreamWindowOverflow);
        Assert.Equal("NCP-ENC-NOT-NEGOTIATED",     NcpErrorCodes.EncNotNegotiated);
        Assert.Equal("NCP-ENC-AUTH-FAILED",        NcpErrorCodes.EncAuthFailed);
    }

    [Fact]
    public void NcpErrorCodes_ExistingCodesUnchanged()
    {
        Assert.Equal("NCP-ANCHOR-NOT-FOUND",     NcpErrorCodes.AnchorNotFound);
        Assert.Equal("NCP-ANCHOR-SCHEMA-INVALID", NcpErrorCodes.AnchorSchemaInvalid);
        Assert.Equal("NCP-STREAM-SEQ-GAP",        NcpErrorCodes.StreamSeqGap);
        Assert.Equal("NCP-ENCODING-UNSUPPORTED",  NcpErrorCodes.EncodingUnsupported);
    }

    [Fact]
    public void NpsStatusCodes_ProtoVersionIncompatible_Present()
    {
        Assert.Equal("NPS-PROTO-VERSION-INCOMPATIBLE", NpsStatusCodes.ProtoVersionIncompatible);
    }

    // ── v0.4 Exceptions ──────────────────────────────────────────────────────

    [Fact]
    public void NpsVersionIncompatibleException_HasCorrectCodes()
    {
        var ex = new NpsVersionIncompatibleException("0.5", "0.4");
        Assert.Equal(NpsStatusCodes.ProtoVersionIncompatible, ex.NpsStatusCode);
        Assert.Equal(NcpErrorCodes.VersionIncompatible,       ex.ProtocolErrorCode);
        Assert.Equal("0.5", ex.ClientMinVersion);
        Assert.Equal("0.4", ex.ServerNpsVersion);
        Assert.Contains("0.5", ex.Message);
        Assert.Contains("0.4", ex.Message);
    }

    [Fact]
    public void NpsDiffFormatUnsupportedException_HasCorrectCodes()
    {
        var ex = new NpsDiffFormatUnsupportedException(NcpPatchFormat.BinaryBitset);
        Assert.Equal(NpsStatusCodes.ClientBadFrame,          ex.NpsStatusCode);
        Assert.Equal(NcpErrorCodes.DiffFormatUnsupported,   ex.ProtocolErrorCode);
        Assert.Equal(NcpPatchFormat.BinaryBitset,            ex.PatchFormat);
        Assert.Contains("binary_bitset", ex.Message);
    }

    [Fact]
    public void NpsAnchorStaleException_HasCorrectCodes()
    {
        const string anchorId = "sha256:" + "a";
        var ex = new NpsAnchorStaleException(anchorId);
        Assert.Equal(NpsStatusCodes.ClientConflict,  ex.NpsStatusCode);
        Assert.Equal(NcpErrorCodes.AnchorStale,      ex.ProtocolErrorCode);
        Assert.Equal(anchorId,                       ex.AnchorId);
    }

    // ── NcpPatchFormat constants ─────────────────────────────────────────────

    [Fact]
    public void NcpPatchFormat_Constants_MatchSpec()
    {
        Assert.Equal("json_patch",    NcpPatchFormat.JsonPatch);
        Assert.Equal("binary_bitset", NcpPatchFormat.BinaryBitset);
    }
}
