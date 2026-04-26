// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NWP.ActionNode;

namespace NPS.NWP.ComplexNode;

/// <summary>
/// Validates child-node URLs before the Complex Node dereferences them
/// (NPS-2 §13.2). Returns <c>null</c> on success, otherwise a human-readable
/// error string; the middleware maps the failure to <c>NWP-AUTH-NID-SCOPE-VIOLATION</c>.
///
/// <list type="bullet">
///   <item>MUST be an <c>https://</c> URL.</item>
///   <item>MUST start with one of the configured allowed prefixes (if any).</item>
///   <item>MUST NOT resolve to loopback / RFC1918 / link-local when <paramref name="rejectPrivate"/> is <c>true</c>.</item>
/// </list>
/// </summary>
public static class ComplexChildUrlValidator
{
    /// <summary>
    /// Validate a single child URL. Returns <c>null</c> when the URL is acceptable,
    /// otherwise a human-readable reason.
    /// </summary>
    public static string? Validate(
        string                     childUrl,
        IReadOnlyList<string>      allowedPrefixes,
        bool                       rejectPrivate = true,
        bool                       allowHttp     = false)
    {
        if (string.IsNullOrWhiteSpace(childUrl))
            return "child node URL must not be empty.";

        if (!Uri.TryCreate(childUrl, UriKind.Absolute, out var uri))
            return $"child node URL '{childUrl}' is not a valid absolute URI.";

        var isHttps = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var isHttp  = string.Equals(uri.Scheme, "http",  StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !(allowHttp && isHttp))
            return $"child node URL MUST use the https:// scheme (got '{uri.Scheme}://').";

        if (allowedPrefixes.Count > 0)
        {
            var matched = false;
            foreach (var prefix in allowedPrefixes)
            {
                if (childUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched)
                return $"child node URL '{childUrl}' is not in the allowed prefix list.";
        }

        if (rejectPrivate && ActionCallbackValidator.IsPrivateHost(uri.Host))
            return $"child node host '{uri.Host}' resolves to a private or loopback address (SSRF guard).";

        return null;
    }
}
