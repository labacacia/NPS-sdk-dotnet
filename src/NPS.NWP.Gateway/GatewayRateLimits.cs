// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace NPS.NWP.Gateway;

/// <summary>
/// Rate limit declaration published in the NWM <c>rate_limits</c> block
/// (NPS-AaaS §2.3). Values of <c>0</c> mean "no limit" for that dimension.
/// </summary>
public sealed class GatewayRateLimits
{
    /// <summary>Maximum requests per minute per consumer NID. 0 = unlimited.</summary>
    [JsonPropertyName("requests_per_minute")]
    public uint RequestsPerMinute { get; init; }

    /// <summary>Maximum concurrent in-flight requests per consumer NID. 0 = unlimited.</summary>
    [JsonPropertyName("max_concurrent")]
    public uint MaxConcurrent { get; init; }

    /// <summary>NPT ceiling per consumer NID per rolling hour. 0 = unlimited.</summary>
    [JsonPropertyName("npt_per_hour")]
    public uint NptPerHour { get; init; }
}
