// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using NPS.NDP.Frames;
using NPS.NDP.Models;

namespace NPS.NDP.Registry;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="INdpRegistry"/>.
///
/// <para>TTL expiry is evaluated lazily on every read (no background timer).
/// Evicted entries are removed during <see cref="GetAll"/> and <see cref="Resolve"/> calls.</para>
///
/// <para>Security profile enforcement (NPS-4 §7.2) and activation_mode TTL capping
/// (NPS-4 §3.1.7.1) are applied in <see cref="Announce"/>.</para>
/// </summary>
public sealed class InMemoryNdpRegistry : INdpRegistry
{
    // NID → (frame, absolute expiry time)
    private readonly Dictionary<string, (AnnounceFrame Frame, DateTime ExpiresAt)> _store = new();
    private readonly Lock _lock = new();
    private readonly NdpRegistryOptions _opts;

    /// <summary>
    /// Creates a registry with default options (<see cref="SecurityProfile.LocalDev"/>,
    /// no address enforcement).
    /// </summary>
    public InMemoryNdpRegistry() : this(new NdpRegistryOptions()) { }

    /// <summary>Creates a registry with the specified options.</summary>
    public InMemoryNdpRegistry(NdpRegistryOptions options)
    {
        _opts = options;
    }

    /// <summary>Optional clock override (for unit tests).</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    // ── INdpRegistry ──────────────────────────────────────────────────────────

    /// <summary>
    /// Registers or refreshes a node/agent announcement (NPS-4 §3.1).
    ///
    /// <para>Two pre-store checks are applied when a security profile is configured:</para>
    /// <list type="bullet">
    ///   <item><see cref="SecurityProfile.OrgPrivate"/>: rejects frames whose first address
    ///         is a globally-routable IP.</item>
    ///   <item><see cref="SecurityProfile.PublicFederated"/>: rejects frames whose first address
    ///         is a private or loopback IP.</item>
    /// </list>
    ///
    /// <para><see cref="Models.ActivationMode.Ephemeral"/> frames have their effective TTL
    /// capped at <see cref="NdpConstants.EphemeralMaxTtlSeconds"/> (NPS-4 §3.1.7.1).</para>
    /// </summary>
    /// <exception cref="NdpAnnounceRejectedException">
    /// Thrown when the frame violates the configured security profile.
    /// </exception>
    public void Announce(AnnounceFrame frame)
    {
        lock (_lock)
        {
            if (frame.Ttl == 0)
            {
                // TTL 0 = orderly shutdown — evict immediately
                _store.Remove(frame.Nid);
                return;
            }

            // Security profile enforcement (NPS-4 §7.2)
            EnforceSecurityProfile(frame);

            // Activation-mode TTL cap (NPS-4 §3.1.7.1)
            uint effectiveTtl = frame.ActivationMode == ActivationMode.Ephemeral
                ? Math.Min(frame.Ttl, NdpConstants.EphemeralMaxTtlSeconds)
                : frame.Ttl;

            var expiresAt = Clock().AddSeconds(effectiveTtl);
            _store[frame.Nid] = (frame, expiresAt);
        }
    }

    /// <inheritdoc/>
    public NdpResolveResult? Resolve(string target)
    {
        // Extract the authority (host) from the nwp:// URL for matching.
        // A node NID contains the authority segment; we match by NID prefix convention
        // and return the first live entry whose addresses cover the target.
        var now = Clock();
        lock (_lock)
        {
            Purge(now);
            foreach (var (frame, _) in _store.Values)
            {
                if (!NwpTargetMatchesNid(frame.Nid, target)) continue;
                var addr = frame.Addresses.FirstOrDefault();
                if (addr is null) continue;
                return new NdpResolveResult
                {
                    Host = addr.Host,
                    Port = addr.Port,
                    Ttl  = frame.Ttl,
                };
            }
        }
        return null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<AnnounceFrame> GetAll()
    {
        var now = Clock();
        lock (_lock)
        {
            Purge(now);
            return _store.Values.Select(e => e.Frame).ToList();
        }
    }

    /// <inheritdoc/>
    public AnnounceFrame? GetByNid(string nid)
    {
        var now = Clock();
        lock (_lock)
        {
            if (!_store.TryGetValue(nid, out var entry)) return null;
            if (entry.ExpiresAt <= now)
            {
                _store.Remove(nid);
                return null;
            }
            return entry.Frame;
        }
    }

    /// <inheritdoc/>
    public NdpResolveResult? ResolveViaDns(string target, IDnsTxtLookup? dnsLookup = null)
    {
        // 1. Try the in-memory registry first.
        var cached = Resolve(target);
        if (cached is not null)
            return cached;

        // 2. Extract the authority (host) from the nwp:// URL.
        var host = ExtractHost(target);
        if (host is null)
            return null;

        // 3. Fall back to DNS TXT lookup on _nps-node.{host}.
        var lookup = dnsLookup ?? new SystemDnsTxtLookup();
        var txtRecords = lookup.Lookup($"_nps-node.{host}");
        foreach (var txt in txtRecords)
        {
            var result = ParseNpsTxtRecord(txt, host);
            if (result is not null)
                return result;
        }

        return null;
    }

    /// <summary>
    /// Parses a single NPS DNS TXT record string (NPS-4 §5) and returns a
    /// <see cref="NdpResolveResult"/>, or <c>null</c> if the record is invalid.
    ///
    /// <para>Required keys: <c>v=nps1</c>, <c>nid</c>.
    /// Optional keys: <c>port</c> (default 17433), <c>type</c>, <c>fp</c>.</para>
    /// </summary>
    /// <param name="txt">Full TXT record string, e.g. <c>"v=nps1 type=memory port=17434 nid=urn:nps:... fp=sha256:..."</c>.</param>
    /// <param name="host">Hostname the TXT record was fetched for (used as the resolved host).</param>
    public static NdpResolveResult? ParseNpsTxtRecord(string txt, string host)
    {
        if (string.IsNullOrWhiteSpace(txt))
            return null;

        var kvs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in txt.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            var key   = token[..eq];
            var value = token[(eq + 1)..];
            kvs[key] = value;
        }

        // v= must be present and equal to "nps1"
        if (!kvs.TryGetValue("v", out var v) || !string.Equals(v, "nps1", StringComparison.OrdinalIgnoreCase))
            return null;

        // nid= is required
        if (!kvs.TryGetValue("nid", out var nid) || string.IsNullOrWhiteSpace(nid))
            return null;

        // port= is optional; default 17433
        int port = 17433;
        if (kvs.TryGetValue("port", out var portStr) && int.TryParse(portStr, out var parsedPort))
            port = parsedPort;

        // fp= is optional
        kvs.TryGetValue("fp", out var fp);

        return new NdpResolveResult
        {
            Host            = host,
            Port            = port,
            Ttl             = NdpConstants.DnsFallbackTtlSeconds,
            CertFingerprint = string.IsNullOrWhiteSpace(fp) ? null : fp,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Enforces the configured security profile against an announce frame's addresses.
    /// Caller MUST hold <c>_lock</c>.
    /// </summary>
    /// <exception cref="NdpAnnounceRejectedException">Thrown when the profile is violated.</exception>
    private void EnforceSecurityProfile(AnnounceFrame frame)
    {
        if (_opts.SecurityProfile == SecurityProfile.LocalDev)
            return; // no enforcement

        foreach (var addr in frame.Addresses)
        {
            bool isPrivate = IsPrivateOrLoopback(addr.Host);

            if (_opts.SecurityProfile == SecurityProfile.OrgPrivate && !isPrivate)
            {
                throw new NdpAnnounceRejectedException(
                    NdpErrorCodes.AnnounceProfileViolation,
                    $"Security profile '{SecurityProfile.OrgPrivate}' rejected address '{addr.Host}' " +
                    $"for NID '{frame.Nid}': only private/loopback addresses are permitted.");
            }

            if (_opts.SecurityProfile == SecurityProfile.PublicFederated && isPrivate)
            {
                throw new NdpAnnounceRejectedException(
                    NdpErrorCodes.AnnounceProfileViolation,
                    $"Security profile '{SecurityProfile.PublicFederated}' rejected address '{addr.Host}' " +
                    $"for NID '{frame.Nid}': private/loopback addresses are not permitted.");
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="host"/> resolves to a private or loopback address.
    /// Checks RFC-1918, RFC-4193 (ULA), link-local, and loopback ranges.
    /// If the host is a hostname (not parseable as IP), returns <c>false</c> (assume public).
    /// </summary>
    private static bool IsPrivateOrLoopback(string host)
    {
        if (!IPAddress.TryParse(host, out var ip))
            return false; // hostname — assume public

        if (IPAddress.IsLoopback(ip))
            return true;

        // Map IPv4-mapped IPv6 to IPv4 for simpler checks
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // IPv4: 10.x, 172.16-31.x, 192.168.x, 169.254.x
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }
        else
        {
            // IPv6: fc00::/7 (ULA), fe80::/10 (link-local)
            return (bytes[0] & 0xFE) == 0xFC   // fc00::/7 (ULA)
                || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80); // fe80::/10
        }
    }

    // Caller must hold _lock.
    private void Purge(DateTime now)
    {
        var expired = _store
            .Where(kv => kv.Value.ExpiresAt <= now)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired)
            _store.Remove(key);
    }

    /// <summary>
    /// Extracts the authority (hostname) from a <c>nwp://</c> URL.
    /// Returns <c>null</c> for malformed URLs.
    /// </summary>
    private static string? ExtractHost(string target)
    {
        if (!target.StartsWith("nwp://", StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = target["nwp://".Length..];
        var slashIdx = rest.IndexOf('/');
        var authority = slashIdx < 0 ? rest : rest[..slashIdx];
        return string.IsNullOrWhiteSpace(authority) ? null : authority;
    }

    /// <summary>
    /// Checks whether a node NID "covers" a <c>nwp://</c> target URL.
    /// Convention: a node NID of the form <c>urn:nps:node:{authority}:{name}</c>
    /// covers any target whose authority matches.
    /// E.g. NID <c>urn:nps:node:api.example.com:products</c> covers
    /// <c>nwp://api.example.com/products</c> and <c>nwp://api.example.com/products/123</c>.
    /// </summary>
    public static bool NwpTargetMatchesNid(string nid, string target)
    {
        // Extract authority from nwp:// URL
        if (!target.StartsWith("nwp://", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = target["nwp://".Length..];
        var slashIdx = rest.IndexOf('/');
        var authority = slashIdx < 0 ? rest : rest[..slashIdx];
        var path      = slashIdx < 0 ? string.Empty : rest[slashIdx..].TrimEnd('/');

        // NID format: urn:nps:{entity_type}:{domain}:{name}
        // The NID authority segment is the 4th colon-delimited component.
        var parts = nid.Split(':');
        if (parts.Length < 5) return false;   // urn : nps : type : domain : name
        var nidAuthority = parts[3];
        var nidName      = parts[4];

        if (!string.Equals(authority, nidAuthority, StringComparison.OrdinalIgnoreCase))
            return false;

        // Path match: target path must start with /{nidName} (or exact)
        var expectedPathPrefix = "/" + nidName;
        return path.StartsWith(expectedPathPrefix, StringComparison.OrdinalIgnoreCase)
               && (path.Length == expectedPathPrefix.Length
                   || path[expectedPathPrefix.Length] == '/');
    }
}
