// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Exceptions;
using NPS.Core.Frames;

namespace NPS.Tests.Ncp;

public sealed class FrameHeaderTests
{
    // ── Parse — default (4-byte) header ─────────────────────────────────────

    [Fact]
    public void Parse_DefaultHeader_ReturnsCorrectFields()
    {
        // Byte 0 = Anchor (0x01), Byte 1 = Tier2MsgPack|Final (0x05), Bytes 2-3 = 42 big-endian
        byte[] wire = [0x01, 0x05, 0x00, 0x2A];
        var h = FrameHeader.Parse(wire);

        Assert.Equal(FrameType.Anchor,       h.FrameType);
        Assert.Equal(FrameFlags.Tier2MsgPack | FrameFlags.Final, h.Flags);
        Assert.Equal(42u,                    h.PayloadLength);
        Assert.False(h.IsExtended);
        Assert.Equal(FrameHeader.DefaultSize, h.HeaderSize);
        Assert.Equal(EncodingTier.MsgPack,   h.EncodingTier);
        Assert.True(h.IsFinal);
        Assert.False(h.IsEncrypted);
    }

    [Fact]
    public void Parse_DefaultHeader_JsonTier_ReturnsJson()
    {
        byte[] wire = [0x04, 0x00, 0x01, 0x00]; // Caps, Tier1Json, length=256
        var h = FrameHeader.Parse(wire);

        Assert.Equal(FrameType.Caps,       h.FrameType);
        Assert.Equal(EncodingTier.Json,    h.EncodingTier);
        Assert.Equal(256u,                 h.PayloadLength);
    }

    [Fact]
    public void Parse_MaxDefaultPayload_Accepted()
    {
        byte[] wire = [0x01, 0x05, 0xFF, 0xFF];
        var h = FrameHeader.Parse(wire);
        Assert.Equal(FrameHeader.DefaultMaxPayload, (int)h.PayloadLength);
    }

    // ── Parse — extended (8-byte) header ────────────────────────────────────

    [Fact]
    public void Parse_ExtendedHeader_ReturnsCorrectFields()
    {
        // EXT flag = bit 7 = 0x80; payload = 0x0001_0000 = 65536 big-endian
        byte[] wire = [0x03, 0x85, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00];
        //                          ^^^^--EXT|Final|MsgPack
        // Flags: 0x85 = 0b1000_0101 = Ext|Final|MsgPack
        var h = FrameHeader.Parse(wire);

        Assert.Equal(FrameType.Stream,        h.FrameType);
        Assert.True(h.IsExtended);
        Assert.Equal(FrameHeader.ExtendedSize, h.HeaderSize);
        Assert.Equal(65536u,                   h.PayloadLength);
    }

    // ── Parse — error cases ──────────────────────────────────────────────────

    [Fact]
    public void Parse_BufferTooShort_ThrowsNpsFrameException()
    {
        Assert.Throws<NpsFrameException>(() => FrameHeader.Parse([0x01]));
    }

    [Fact]
    public void Parse_DefaultHeader_BufferTooShort_ThrowsNpsFrameException()
    {
        // flags has no EXT, but only 3 bytes given
        Assert.Throws<NpsFrameException>(() => FrameHeader.Parse([0x01, 0x00, 0x00]));
    }

    [Fact]
    public void Parse_ExtendedHeader_BufferTooShort_ThrowsNpsFrameException()
    {
        // EXT flag set but only 6 bytes given (need 8)
        Assert.Throws<NpsFrameException>(() => FrameHeader.Parse([0x01, 0x80, 0x00, 0x00, 0x00, 0x00]));
    }

    // ── WriteTo — default ────────────────────────────────────────────────────

    [Fact]
    public void WriteTo_DefaultHeader_RoundTrips()
    {
        var original = new FrameHeader(FrameType.Anchor, FrameFlags.Tier2MsgPack | FrameFlags.Final, 1234);
        Span<byte> buf = stackalloc byte[FrameHeader.DefaultSize];
        original.WriteTo(buf);

        var parsed = FrameHeader.Parse(buf);
        Assert.Equal(original.FrameType,     parsed.FrameType);
        Assert.Equal(original.Flags,         parsed.Flags);
        Assert.Equal(original.PayloadLength, parsed.PayloadLength);
    }

    [Fact]
    public void WriteTo_ExtendedHeader_RoundTrips()
    {
        var flags    = FrameFlags.Ext | FrameFlags.Tier2MsgPack | FrameFlags.Final;
        var original = new FrameHeader(FrameType.Stream, flags, 70_000);
        Span<byte> buf = stackalloc byte[FrameHeader.ExtendedSize];
        original.WriteTo(buf);

        // Reserved bytes must be zero
        Assert.Equal(0, buf[6]);
        Assert.Equal(0, buf[7]);

        var parsed = FrameHeader.Parse(buf);
        Assert.Equal(original.FrameType,     parsed.FrameType);
        Assert.Equal(original.PayloadLength, parsed.PayloadLength);
        Assert.True(parsed.IsExtended);
    }

    [Fact]
    public void WriteTo_BufferTooSmall_ThrowsArgumentException()
    {
        var h = new FrameHeader(FrameType.Anchor, FrameFlags.Final, 0);
        Assert.Throws<ArgumentException>(() => h.WriteTo(new byte[2]));
    }

    // ── Encrypted flag ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_EncryptedFlag_IsEncryptedTrue()
    {
        // Flags: ENC = 0x08
        byte[] wire = [0x01, 0x0D, 0x00, 0x10]; // MsgPack|Final|Encrypted, length=16
        var h = FrameHeader.Parse(wire);
        Assert.True(h.IsEncrypted);
    }
}
