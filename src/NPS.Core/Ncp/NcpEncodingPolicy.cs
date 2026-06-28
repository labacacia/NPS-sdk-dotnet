// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Exceptions;
using NPS.Core.Frames;

namespace NPS.Core.Ncp;

/// <summary>
/// Encoding policy negotiated for an established NCP native-mode session.
/// The default tier is stable for ordinary frames; Tier-3 BinaryVector is an
/// optional extension for frame classes that explicitly bind to it.
/// </summary>
public sealed record NcpEncodingPolicy(
    EncodingTier DefaultTier,
    bool BinaryVectorEnabled = false)
{
    public IReadOnlyList<string> EnabledEncodings =>
        BinaryVectorEnabled
            ? [EncodingToken(DefaultTier), "binary_vector.v1"]
            : [EncodingToken(DefaultTier)];

    public bool Allows(EncodingTier tier, FrameType frameType) =>
        tier == DefaultTier ||
        (tier == EncodingTier.BinaryVector && BinaryVectorEnabled && IsBinaryVectorFrame(frameType));

    public void EnsureAllows(FrameHeader header)
    {
        if (Allows(header.EncodingTier, header.FrameType))
            return;

        throw new NpsEncodingUnsupportedException(
            $"Frame type 0x{(byte)header.FrameType:X2} used {EncodingToken(header.EncodingTier)}, " +
            $"but the negotiated session policy allows {string.Join(", ", EnabledEncodings)}.");
    }

    public static NcpEncodingPolicy FromEnabledEncodings(
        EncodingTier defaultTier,
        IReadOnlyList<string>? enabledEncodings) =>
        new(defaultTier, enabledEncodings?.Contains("binary_vector.v1") == true);

    public static string EncodingToken(EncodingTier tier) => tier switch
    {
        EncodingTier.Json         => "json",
        EncodingTier.MsgPack      => "msgpack",
        EncodingTier.BinaryVector => "binary_vector.v1",
        _ => $"unknown:{(byte)tier}"
    };

    private static bool IsBinaryVectorFrame(FrameType frameType) =>
        frameType == FrameType.Query;
}
