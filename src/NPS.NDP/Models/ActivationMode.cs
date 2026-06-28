// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NDP.Models;

/// <summary>
/// Valid values for <c>AnnounceFrame.activation_mode</c> (NPS-4 §3.1.1).
/// </summary>
public static class ActivationMode
{
    /// <summary>Short-lived node; registry caps effective TTL at <see cref="NdpConstants.EphemeralMaxTtlSeconds"/>.</summary>
    public const string Ephemeral = "ephemeral";

    /// <summary>Long-running persistent service; TTL is used as-is (default when omitted).</summary>
    public const string Resident  = "resident";

    /// <summary>Resident base that first accepts pushed frames at <c>activation_endpoint</c>, then can fall back to inbox.</summary>
    public const string Hybrid    = "hybrid";
}
