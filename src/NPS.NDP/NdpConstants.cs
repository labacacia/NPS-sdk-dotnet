// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NDP;

/// <summary>
/// Protocol-level limits defined by NPS-4 §8.
/// </summary>
public static class NdpConstants
{
    /// <summary>Default NDP port for nwp:// endpoints.</summary>
    public const int DefaultPort = 17433;

    /// <summary>
    /// Maximum effective TTL (seconds) applied to ephemeral nodes (NPS-4 §3.1.1).
    /// Even if the AnnounceFrame declares a longer TTL, the registry caps it here.
    /// </summary>
    public const uint EphemeralMaxTtlSeconds = 60;

    /// <summary>Default TTL (seconds) returned for DNS-resolved entries.</summary>
    public const uint DnsFallbackTtlSeconds = 300;
}
