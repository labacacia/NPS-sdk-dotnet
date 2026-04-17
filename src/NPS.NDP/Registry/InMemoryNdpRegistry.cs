// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NDP.Frames;

namespace NPS.NDP.Registry;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="INdpRegistry"/>.
///
/// <para>TTL expiry is evaluated lazily on every read (no background timer).
/// Evicted entries are removed during <see cref="GetAll"/> and <see cref="Resolve"/> calls.</para>
/// </summary>
public sealed class InMemoryNdpRegistry : INdpRegistry
{
    // NID → (frame, absolute expiry time)
    private readonly Dictionary<string, (AnnounceFrame Frame, DateTime ExpiresAt)> _store = new();
    private readonly Lock _lock = new();

    /// <summary>Optional clock override (for unit tests).</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    // ── INdpRegistry ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
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

            var expiresAt = Clock().AddSeconds(frame.Ttl);
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

    // ── Helpers ───────────────────────────────────────────────────────────────

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
