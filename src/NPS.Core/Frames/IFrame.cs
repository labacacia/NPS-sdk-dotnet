// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Core.Frames;

/// <summary>
/// Base contract for all NPS frames across NCP / NWP / NIP / NDP / NOP.
/// </summary>
public interface IFrame
{
    /// <summary>Single-byte frame type code per the unified frame namespace (NPS-0 §9).</summary>
    FrameType FrameType { get; }

    /// <summary>Preferred encoding tier. Codecs may override via the <c>overrideTier</c> parameter.</summary>
    EncodingTier PreferredTier { get; }
}
