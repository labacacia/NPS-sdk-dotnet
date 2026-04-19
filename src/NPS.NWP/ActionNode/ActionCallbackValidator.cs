// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;

namespace NPS.NWP.ActionNode;

/// <summary>
/// Validates <see cref="NPS.NWP.Frames.ActionFrame.CallbackUrl"/> per NPS-2 §7.1.
/// <list type="bullet">
///   <item>MUST be an <c>https://</c> URL.</item>
///   <item>SHOULD NOT target a private / loopback address (SSRF guard).</item>
/// </list>
/// This is a local mirror of <c>NPS.NOP.Validation.NopCallbackValidator</c> to avoid
/// a cross-protocol project reference.
/// </summary>
public static class ActionCallbackValidator
{
    /// <summary>Returns <c>null</c> when <paramref name="callbackUrl"/> is valid,
    /// otherwise a human-readable error string.</summary>
    public static string? Validate(string callbackUrl, bool rejectPrivate = true)
    {
        if (string.IsNullOrWhiteSpace(callbackUrl))
            return "callback_url must not be empty.";

        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri))
            return $"callback_url '{callbackUrl}' is not a valid absolute URI.";

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return $"callback_url MUST use the https:// scheme (got '{uri.Scheme}://').";

        if (rejectPrivate && IsPrivateHost(uri.Host))
            return $"callback_url host '{uri.Host}' resolves to a private or loopback address (SSRF guard).";

        return null;
    }

    /// <summary>Detects hostname literals and IP-literal addresses that fall within
    /// loopback / link-local / RFC1918 ranges. DNS resolution is intentionally avoided.</summary>
    public static bool IsPrivateHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        var stripped = host.TrimStart('[').TrimEnd(']');
        if (!IPAddress.TryParse(stripped, out var ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return
                b[0] == 127                                        ||
                b[0] == 10                                         ||
                b[0] == 0                                          ||
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31)          ||
                (b[0] == 192 && b[1] == 168)                       ||
                (b[0] == 169 && b[1] == 254);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
        }

        return false;
    }
}
