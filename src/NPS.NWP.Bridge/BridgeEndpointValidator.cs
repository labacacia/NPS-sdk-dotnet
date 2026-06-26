// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NWP.ActionNode;

namespace NPS.NWP.Bridge;

/// <summary>Validates outbound Bridge endpoints before dereferencing them.</summary>
public static class BridgeEndpointValidator
{
    /// <summary>
    /// Parse and validate an HTTP(S) Bridge endpoint. By default, both
    /// <c>http://</c> and <c>https://</c> are accepted, while private and
    /// loopback hosts are rejected as an SSRF guard.
    /// </summary>
    public static Uri ParseHttpEndpoint(BridgeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new BridgeDispatchException(
                BridgeErrorCodes.EndpointInvalid,
                "bridge_target.endpoint must be an absolute http:// or https:// URI.");
        }

        var allowHttp = GetBool(target, "allow_http", defaultValue: true);
        if (!allowHttp && uri.Scheme == Uri.UriSchemeHttp)
        {
            throw new BridgeDispatchException(
                BridgeErrorCodes.EndpointInvalid,
                "bridge_target.endpoint MUST use https:// unless bridge_target.allow_http is true.");
        }

        var allowedPrefixes = GetStringList(target, "allowed_prefixes");
        if (allowedPrefixes.Count > 0 &&
            !allowedPrefixes.Any(prefix => MatchesAllowedPrefix(uri, prefix)))
        {
            throw new BridgeDispatchException(
                BridgeErrorCodes.EndpointInvalid,
                $"bridge_target.endpoint '{target.Endpoint}' is not in bridge_target.allowed_prefixes.");
        }

        var rejectPrivate = GetBool(target, "reject_private", defaultValue: true);
        if (rejectPrivate && ActionCallbackValidator.IsPrivateHost(uri.Host))
        {
            throw new BridgeDispatchException(
                BridgeErrorCodes.EndpointInvalid,
                $"bridge_target.endpoint host '{uri.Host}' is private or loopback (SSRF guard).");
        }

        return uri;
    }

    private static bool GetBool(BridgeTarget target, string name, bool defaultValue)
    {
        if (!BridgeTargetParser.TryGetJson(target, name, out var value))
            return defaultValue;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    private static IReadOnlyList<string> GetStringList(BridgeTarget target, string name)
    {
        if (!BridgeTargetParser.TryGetJson(target, name, out var value))
            return Array.Empty<string>();

        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return new[] { value.GetString()! };

        if (value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                items.Add(item.GetString()!);
        }

        return items;
    }

    private static bool MatchesAllowedPrefix(Uri endpoint, string rawPrefix)
    {
        if (!Uri.TryCreate(rawPrefix, UriKind.Absolute, out var prefix))
            return false;

        if (!string.Equals(endpoint.Scheme, prefix.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(endpoint.IdnHost, prefix.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            endpoint.Port != prefix.Port)
        {
            return false;
        }

        var prefixPath = prefix.AbsolutePath;
        if (prefixPath == "/")
            return true;

        var endpointPath = endpoint.AbsolutePath;
        if (!endpointPath.StartsWith(prefixPath, StringComparison.OrdinalIgnoreCase))
            return false;

        return endpointPath.Length == prefixPath.Length ||
               prefixPath.EndsWith("/", StringComparison.Ordinal) ||
               endpointPath[prefixPath.Length] == '/';
    }
}
