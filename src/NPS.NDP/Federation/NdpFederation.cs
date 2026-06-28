// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NDP;

/// <summary>Helpers for NDP cross-registry federation forwarding (NPS-4 §7.6).</summary>
public static class NdpFederation
{
    /// <summary>HTTP header carrying comma-separated federation hop NIDs.</summary>
    public const string ForwardedByHeader = "ndp-forwarded-by";

    /// <summary>Maximum federation hops before a forwarded AnnounceFrame is silently dropped.</summary>
    public const int MaxFederationHops = 3;

    /// <summary>Parse the <c>ndp-forwarded-by</c> header into trimmed NID hops.</summary>
    public static IReadOnlyList<string> ParseForwardedBy(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return Array.Empty<string>();

        return header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Append this registry's NID, return <c>null</c> for the 3-hop silent-drop case,
    /// or throw <see cref="ArgumentException"/> with <see cref="NdpErrorCodes.FederationLoop"/>
    /// when a loop is detected.
    /// </summary>
    public static string? AppendForwardedBy(string ownNid, string? header)
    {
        var hops = ParseForwardedBy(header);
        if (hops.Contains(ownNid, StringComparer.Ordinal))
            throw new ArgumentException(
                $"{NdpErrorCodes.FederationLoop}: own NID already appears in ndp-forwarded-by");

        if (hops.Count >= MaxFederationHops)
            return null;

        return string.Join(", ", hops.Concat([ownNid]));
    }
}
