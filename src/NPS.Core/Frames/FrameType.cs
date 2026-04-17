// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Core.Frames;

/// <summary>
/// Unified frame byte namespace for the full NPS suite (NPS-0 §9).
/// </summary>
public enum FrameType : byte
{
    // ── NCP  0x01–0x0F ──────────────────────────────────────────────
    /// <summary>Schema anchor — establishes a global schema reference (SHA-256 keyed).</summary>
    Anchor      = 0x01,
    /// <summary>Incremental patch — transmits only changed fields.</summary>
    Diff        = 0x02,
    /// <summary>Streaming chunk — ordered data block with back-pressure support.</summary>
    Stream      = 0x03,
    /// <summary>Capsule — full response envelope referencing an anchor.</summary>
    Caps        = 0x04,
    /// <summary>Align — multi-AI state sync (upgraded to NOP AlignStream in v0.2+).</summary>
    Align       = 0x05,
    /// <summary>Hello — native-mode client handshake; declares NPS version and capabilities (NPS-1 §4.6).</summary>
    Hello       = 0x06,

    // ── NWP  0x10–0x1F ──────────────────────────────────────────────
    /// <summary>Structured data query targeting a Memory Node.</summary>
    Query       = 0x10,
    /// <summary>Operation invocation targeting an Action or Complex Node.</summary>
    Action      = 0x11,

    // ── NIP  0x20–0x2F ──────────────────────────────────────────────
    /// <summary>Agent identity declaration carrying NID certificate.</summary>
    Ident       = 0x20,
    /// <summary>Cross-CA trust chain delegation.</summary>
    Trust       = 0x21,
    /// <summary>Revoke a NID or capability grant.</summary>
    Revoke      = 0x22,

    // ── NDP  0x30–0x3F ──────────────────────────────────────────────
    /// <summary>Node / Agent capability broadcast.</summary>
    Announce    = 0x30,
    /// <summary>Resolve a nwp:// address to a physical endpoint.</summary>
    Resolve     = 0x31,
    /// <summary>Node graph sync and change subscription.</summary>
    Graph       = 0x32,

    // ── NOP  0x40–0x4F ──────────────────────────────────────────────
    /// <summary>Task definition and dispatch.</summary>
    Task        = 0x40,
    /// <summary>Sub-task delegation to another Agent.</summary>
    Delegate    = 0x41,
    /// <summary>Multi-Agent state synchronisation point.</summary>
    Sync        = 0x42,
    /// <summary>Directed task stream — AlignStream (NCP AlignFrame upgrade).</summary>
    AlignStream = 0x43,

    // ── Reserved / System  0xF0–0xFF ────────────────────────────────
    /// <summary>Unified error frame — all protocol layers (NPS-1 §4.6).</summary>
    Error       = 0xFE,
}
