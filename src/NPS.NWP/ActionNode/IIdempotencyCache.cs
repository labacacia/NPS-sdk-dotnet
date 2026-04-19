// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace NPS.NWP.ActionNode;

/// <summary>
/// A cached idempotent response (NPS-2 §7.1). Preserves the original result so a
/// repeated request with the same <c>idempotency_key</c> can be served without
/// re-executing the action.
/// </summary>
public sealed class IdempotentEntry
{
    /// <summary>Action id the original request targeted.</summary>
    public required string ActionId { get; init; }

    /// <summary>Hash of the original <c>params</c> payload. Used to detect key reuse
    /// with different parameters (<c>NWP-ACTION-IDEMPOTENCY-CONFLICT</c>).</summary>
    public required string ParamsHash { get; init; }

    /// <summary>Stored execution result (for synchronous actions).</summary>
    public JsonElement? Result { get; init; }

    /// <summary>anchor_ref used on the original response, if any.</summary>
    public string? AnchorRef { get; init; }

    /// <summary>Task id for async actions — the second request gets the same task handle.</summary>
    public string? TaskId { get; init; }

    /// <summary>UTC timestamp at which this entry expires.</summary>
    public required DateTime ExpiresAt { get; init; }
}

/// <summary>
/// Cache of idempotent action results, keyed by <c>(action_id, idempotency_key)</c>
/// with a TTL matching <see cref="ActionNodeOptions.IdempotencyTtl"/> (NPS-2 §7.1).
/// </summary>
public interface IIdempotencyCache
{
    /// <summary>
    /// Look up an entry. Returns <c>null</c> if absent or expired. Implementations
    /// SHOULD purge expired entries opportunistically.
    /// </summary>
    IdempotentEntry? Get(string actionId, string idempotencyKey);

    /// <summary>
    /// Store a new entry. If an entry with the same key already exists and its
    /// <c>ParamsHash</c> differs, this MUST return <c>false</c> so the caller can
    /// raise <c>NWP-ACTION-IDEMPOTENCY-CONFLICT</c>. An identical hash re-stores silently.
    /// </summary>
    bool TryStore(string actionId, string idempotencyKey, IdempotentEntry entry);

    /// <summary>Purge expired entries. Returns the number removed.</summary>
    int PurgeExpired(DateTime now);
}
