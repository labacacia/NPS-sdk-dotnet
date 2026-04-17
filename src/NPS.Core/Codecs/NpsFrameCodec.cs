// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Registry;

namespace NPS.Core.Codecs;

/// <summary>
/// Top-level codec dispatcher. Reads the <see cref="EncodingTier"/> from the frame header
/// flags and routes to the appropriate <see cref="IFrameCodec"/> implementation.
/// Supports both default (4-byte) and extended (8-byte, EXT=1) header modes (NPS-1 §3.1).
/// </summary>
public sealed class NpsFrameCodec
{
    private readonly Tier1JsonCodec    _json;
    private readonly Tier2MsgPackCodec _msgpack;
    private readonly FrameRegistry     _registry;
    private readonly uint              _maxPayload;

    public NpsFrameCodec(
        Tier1JsonCodec    json,
        Tier2MsgPackCodec msgpack,
        FrameRegistry     registry)
        : this(json, msgpack, registry, FrameHeader.DefaultMaxPayload) { }

    public NpsFrameCodec(
        Tier1JsonCodec    json,
        Tier2MsgPackCodec msgpack,
        FrameRegistry     registry,
        uint              maxPayload)
    {
        _json       = json;
        _msgpack    = msgpack;
        _registry   = registry;
        _maxPayload = maxPayload;
    }

    /// <summary>
    /// Serialises <paramref name="frame"/> to a complete wire message:
    /// header + tier-encoded payload.
    /// <para>
    /// When the payload exceeds 64 KiB but is within <c>maxPayload</c>,
    /// the EXT flag is set automatically and an 8-byte header is emitted.
    /// </para>
    /// </summary>
    public byte[] Encode(IFrame frame, EncodingTier? overrideTier = null)
    {
        var tier    = overrideTier ?? frame.PreferredTier;
        var codec   = SelectCodec(tier);
        var payload = codec.Encode(frame);

        if ((uint)payload.Length > _maxPayload)
            throw new NpsCodecException(
                $"Encoded payload for {frame.FrameType} exceeds max_frame_payload ({payload.Length} bytes > {_maxPayload}). " +
                "Use StreamFrame (0x03) for large payloads.");

        bool useExt = payload.Length > FrameHeader.DefaultMaxPayload;
        var flags = BuildFlags(frame, tier);
        if (useExt)
            flags |= FrameFlags.Ext;

        int headerSize = useExt ? FrameHeader.ExtendedSize : FrameHeader.DefaultSize;
        var wire = new byte[headerSize + payload.Length];
        var header = new FrameHeader(frame.FrameType, flags, (uint)payload.Length);

        header.WriteTo(wire.AsSpan());
        payload.CopyTo(wire, headerSize);
        return wire;
    }

    /// <summary>
    /// Parses a complete wire message into a strongly-typed <see cref="IFrame"/>.
    /// Handles both default and extended header modes.
    /// </summary>
    public IFrame Decode(ReadOnlySpan<byte> wire)
    {
        var header  = FrameHeader.Parse(wire);
        var payload = wire.Slice(header.HeaderSize, (int)header.PayloadLength);
        var codec   = SelectCodec(header.EncodingTier);
        return codec.Decode(header.FrameType, payload, _registry);
    }

    /// <summary>Decodes only the header without deserialising the payload. Useful for routing.</summary>
    public static FrameHeader PeekHeader(ReadOnlySpan<byte> wire) => FrameHeader.Parse(wire);

    // ── Private helpers ──────────────────────────────────────────────────────

    private static FrameFlags BuildFlags(IFrame frame, EncodingTier tier)
    {
        var flags = tier == EncodingTier.Json ? FrameFlags.Tier1Json : FrameFlags.Tier2MsgPack;

        bool isFinal = frame is not StreamFrame sf || sf.IsLast;
        if (isFinal)
            flags |= FrameFlags.Final;

        return flags;
    }

    private IFrameCodec SelectCodec(EncodingTier tier) => tier switch
    {
        EncodingTier.Json    => _json,
        EncodingTier.MsgPack => _msgpack,
        _ => throw new NpsCodecException($"Unsupported encoding tier: {tier} (0x{(byte)tier:X2}).")
    };
}
