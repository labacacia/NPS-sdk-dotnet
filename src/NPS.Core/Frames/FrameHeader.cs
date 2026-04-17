// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using NPS.Core.Exceptions;

namespace NPS.Core.Frames;

/// <summary>
/// Frame header present at the start of every NPS wire message (NPS-1 §3.1).
/// <para>
/// <b>Default (4 bytes, EXT=0):</b>
/// <code>
/// Byte 0   : FrameType
/// Byte 1   : Flags
/// Byte 2–3 : PayloadLength (big-endian uint16, max 65535)
/// </code>
/// </para>
/// <para>
/// <b>Extended (8 bytes, EXT=1):</b>
/// <code>
/// Byte 0   : FrameType
/// Byte 1   : Flags (bit 7 = 1)
/// Byte 2–5 : PayloadLength (big-endian uint32, max 4 GB)
/// Byte 6–7 : Reserved (must be 0)
/// </code>
/// </para>
/// </summary>
public readonly record struct FrameHeader(
    FrameType FrameType,
    FrameFlags Flags,
    uint PayloadLength)
{
    /// <summary>Default (compact) header size in bytes.</summary>
    public const int DefaultSize = 4;

    /// <summary>Extended header size in bytes (EXT=1).</summary>
    public const int ExtendedSize = 8;

    /// <summary>Maximum payload in default mode (64 KiB - 1).</summary>
    public const int DefaultMaxPayload = ushort.MaxValue;

    /// <summary>Maximum payload in extended mode (4 GiB - 1).</summary>
    public const uint ExtendedMaxPayload = uint.MaxValue;

    /// <summary>True when the EXT flag (bit 7) is set — extended 8-byte header.</summary>
    public bool IsExtended => (Flags & FrameFlags.Ext) != 0;

    /// <summary>Header size in bytes for this instance.</summary>
    public int HeaderSize => IsExtended ? ExtendedSize : DefaultSize;

    /// <summary>Extracts the <see cref="EncodingTier"/> from the lower 2 bits of <see cref="Flags"/>.</summary>
    public EncodingTier EncodingTier => (EncodingTier)((byte)Flags & 0x03);

    /// <summary>True when bit 2 (FINAL) is set.</summary>
    public bool IsFinal => ((byte)Flags & 0x04) != 0;

    /// <summary>True when bit 3 (ENC) is set.</summary>
    public bool IsEncrypted => ((byte)Flags & 0x08) != 0;

    /// <summary>
    /// Parses a frame header from the start of <paramref name="buffer"/>.
    /// Reads 2 bytes first to determine whether the EXT flag is set,
    /// then reads the remaining bytes accordingly.
    /// </summary>
    public static FrameHeader Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2)
            throw new NpsFrameException(
                $"Buffer too small to read frame type and flags: need >= 2 bytes, got {buffer.Length}.");

        var flags = (FrameFlags)buffer[1];
        bool ext = (flags & FrameFlags.Ext) != 0;

        if (ext)
        {
            if (buffer.Length < ExtendedSize)
                throw new NpsFrameException(
                    $"Buffer too small for extended frame header: need {ExtendedSize} bytes, got {buffer.Length}.");

            return new FrameHeader(
                FrameType:     (FrameType)buffer[0],
                Flags:         flags,
                PayloadLength: BinaryPrimitives.ReadUInt32BigEndian(buffer[2..]));
        }

        if (buffer.Length < DefaultSize)
            throw new NpsFrameException(
                $"Buffer too small for frame header: need {DefaultSize} bytes, got {buffer.Length}.");

        return new FrameHeader(
            FrameType:     (FrameType)buffer[0],
            Flags:         flags,
            PayloadLength: BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]));
    }

    /// <summary>
    /// Writes this header into <paramref name="buffer"/>.
    /// Uses 4 bytes (default) or 8 bytes (extended) depending on the EXT flag.
    /// </summary>
    public void WriteTo(Span<byte> buffer)
    {
        int required = IsExtended ? ExtendedSize : DefaultSize;
        if (buffer.Length < required)
            throw new ArgumentException(
                $"Destination buffer must be at least {required} bytes.", nameof(buffer));

        buffer[0] = (byte)FrameType;
        buffer[1] = (byte)Flags;

        if (IsExtended)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer[2..], PayloadLength);
            buffer[6] = 0;
            buffer[7] = 0;
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (ushort)PayloadLength);
        }
    }
}
