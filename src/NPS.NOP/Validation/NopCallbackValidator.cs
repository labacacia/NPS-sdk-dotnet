// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;

namespace NPS.NOP.Validation;

/// <summary>
/// Validates <c>TaskFrame.callback_url</c> per NPS-5 §8.4.
/// <list type="bullet">
///   <item>MUST be an <c>https://</c> URL.</item>
///   <item>SHOULD NOT target a private/loopback address (SSRF guard).</item>
/// </list>
/// </summary>
public static class NopCallbackValidator
{
    /// <summary>
    /// Validates <paramref name="callbackUrl"/>.
    /// Returns <c>null</c> when valid; otherwise returns a human-readable error string.
    /// </summary>
    public static string? ValidateCallbackUrl(string callbackUrl)
    {
        if (string.IsNullOrWhiteSpace(callbackUrl))
            return "callback_url must not be empty.";

        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri))
            return $"callback_url '{callbackUrl}' is not a valid absolute URI.";

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return $"callback_url MUST use the https:// scheme (got '{uri.Scheme}://')." ;

        // SSRF guard: reject well-known private / loopback host strings.
        // DNS resolution is intentionally avoided to keep validation synchronous and
        // free of network I/O; callers should apply additional network-layer controls.
        if (IsPrivateHost(uri.Host))
            return $"callback_url host '{uri.Host}' resolves to a private or loopback address (SSRF guard).";

        return null; // valid
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="host"/> is a well-known private /
    /// loopback / link-local address or hostname without performing DNS resolution.
    /// </summary>
    public static bool IsPrivateHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return true;

        // Reject by hostname literals (case-insensitive)
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        // Try to parse as an IP address directly (IPv4 or IPv6 literal in URI brackets)
        var stripped = host.TrimStart('[').TrimEnd(']'); // IPv6 URI form: [::1]
        if (IPAddress.TryParse(stripped, out var ip))
            return IsPrivateIp(ip);

        return false;
    }

    private static bool IsPrivateIp(IPAddress ip)
    {
        // Normalize IPv4-mapped IPv6 (::ffff:10.0.0.1) to IPv4
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return
                bytes[0] == 127                                              ||  // 127.0.0.0/8 loopback
                bytes[0] == 10                                               ||  // 10.0.0.0/8
                bytes[0] == 0                                                ||  // 0.0.0.0/8
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)       ||  // 172.16.0.0/12
                (bytes[0] == 192 && bytes[1] == 168)                        ||  // 192.168.0.0/16
                (bytes[0] == 169 && bytes[1] == 254);                           // 169.254.0.0/16 link-local
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IsLoopback(ip)   // ::1
                || ip.IsIPv6LinkLocal          // fe80::/10
                || ip.IsIPv6SiteLocal;         // fec0::/10 (deprecated but guard anyway)
        }

        return false;
    }
}
