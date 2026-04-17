// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Core.Frames;

/// <summary>
/// Wire encoding tier, stored in the lower 2 bits of <see cref="FrameFlags"/> (NPS-1 §3.2).
/// </summary>
public enum EncodingTier : byte
{
    /// <summary>Tier-1: UTF-8 JSON. Human-readable; used in development and compatibility mode.</summary>
    Json    = 0x00,
    /// <summary>Tier-2: MessagePack binary. ~60 % size reduction vs JSON; default for production.</summary>
    MsgPack = 0x01,
    // 0x02 = Reserved (formerly Tier-3 MatrixTensor, NPS-1 §3.2)
    // 0x03 = Reserved
}

/// <summary>
/// Flags byte in the 4-byte fixed frame header (NPS-1 §3.2).
/// <code>
/// Bit 7  Bit 6  Bit 5  Bit 4  Bit 3  Bit 2  Bit 1  Bit 0
/// ┌─RSV──┬─RSV──┬─RSV──┬─RSV──┬─ENC──┬FINAL─┬──T1──┬──T0──┐
/// └──────┴──────┴──────┴──────┴──────┴──────┴──────┴──────┘
/// </code>
/// • Bits 0–1 (T0, T1): Encoding tier (see <see cref="EncodingTier"/>).
/// • Bit 2 (FINAL): StreamFrame last-chunk sentinel. Non-stream frames MUST set this to 1.
/// • Bit 3 (ENC): Payload is encrypted. MUST be 1 in production; 0 in dev/plaintext mode.
/// • Bit 7 (EXT): Extended header — 8-byte header with 4-byte payload length.
/// • Bits 4–6 (RSV): Reserved. Sender MUST write 0; receiver MUST ignore.
/// </summary>
[Flags]
public enum FrameFlags : byte
{
    None         = 0x00,

    // ── Encoding tier (bits 0–1) ──────────────────────────────────────────────
    /// <summary>Tier-1 JSON. Same value as <see cref="None"/> — explicit alias for readability.</summary>
    Tier1Json    = 0x00,
    /// <summary>Tier-2 MessagePack binary.</summary>
    Tier2MsgPack = 0x01,

    // ── Feature flags (bits 2–3) ─────────────────────────────────────────────
    /// <summary>
    /// Final chunk flag (bit 2). Set on the last <c>StreamFrame</c> in a stream.
    /// All non-stream frames MUST have this bit set.
    /// </summary>
    Final        = 0x04,
    /// <summary>
    /// Encrypted flag (bit 3). Indicates that the Payload is encrypted.
    /// MUST be set in production; 0 in development / plaintext mode (NPS-1 §7).
    /// </summary>
    Encrypted    = 0x08,

    // ── Extended header (bit 7) ──────────────────────────────────────────────
    /// <summary>
    /// Extended frame header flag (bit 7). When set, the header is 8 bytes:
    /// [FrameType(1) | Flags(1) | PayloadLength(4, BE) | Reserved(2)].
    /// Supports payloads up to 4 GB (NPS-1 §3.1).
    /// </summary>
    Ext          = 0x80,
}
