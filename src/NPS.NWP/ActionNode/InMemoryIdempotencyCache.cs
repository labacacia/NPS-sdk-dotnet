// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace NPS.NWP.ActionNode;

/// <summary>
/// In-process idempotency cache. Suitable for single-instance deployments and tests.
/// Replace with a distributed store (Redis) for multi-instance Action Nodes.
/// </summary>
public sealed class InMemoryIdempotencyCache : IIdempotencyCache
{
    private readonly ConcurrentDictionary<string, IdempotentEntry> _entries = new();

    /// <summary>Injectable clock, defaults to <see cref="DateTime.UtcNow"/>.</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    private static string Key(string actionId, string idempotencyKey) =>
        $"{actionId}\u001f{idempotencyKey}";

    public IdempotentEntry? Get(string actionId, string idempotencyKey)
    {
        var key = Key(actionId, idempotencyKey);
        if (!_entries.TryGetValue(key, out var entry)) return null;
        if (entry.ExpiresAt <= Clock())
        {
            _entries.TryRemove(key, out _);
            return null;
        }
        return entry;
    }

    public bool TryStore(string actionId, string idempotencyKey, IdempotentEntry entry)
    {
        var key = Key(actionId, idempotencyKey);
        var now = Clock();
        while (true)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.ExpiresAt <= now)
                {
                    // expired — evict and retry
                    _entries.TryRemove(new KeyValuePair<string, IdempotentEntry>(key, existing));
                    continue;
                }
                return existing.ParamsHash == entry.ParamsHash;
            }
            if (_entries.TryAdd(key, entry)) return true;
            // race: re-check on next loop iteration
        }
    }

    public int PurgeExpired(DateTime now)
    {
        var purged = 0;
        foreach (var kv in _entries)
        {
            if (kv.Value.ExpiresAt <= now &&
                _entries.TryRemove(new KeyValuePair<string, IdempotentEntry>(kv.Key, kv.Value)))
            {
                purged++;
            }
        }
        return purged;
    }
}
