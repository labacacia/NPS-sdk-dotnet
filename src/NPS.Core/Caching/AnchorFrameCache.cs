// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Caching;

/// <summary>
/// Process-level cache for <see cref="AnchorFrame"/> instances, keyed by SHA-256 anchor_id.
///
/// Design notes:
/// <list type="bullet">
///   <item>anchor_id is the full SHA-256 hex of the canonicalized schema (NPS-1 §4.1) — 64 chars.</item>
///   <item>Idempotent Set(): identical schemas produce the same key; no duplicate entries.</item>
///   <item>Anchor poisoning protection: a second AnchorFrame with the same anchor_id but a different
///     schema is rejected with <see cref="NpsAnchorPoisonException"/> (NPS-1 §7.2).</item>
///   <item>TTL uses sliding expiration so actively-used anchors stay warm without manual refresh.</item>
///   <item>Registered as <b>scoped</b> in DI — each connection/session gets its own logical view,
///     while the underlying <see cref="IMemoryCache"/> is shared across the process.</item>
/// </list>
/// </summary>
public sealed class AnchorFrameCache
{
    private readonly IMemoryCache _cache;

    public AnchorFrameCache(IMemoryCache cache) => _cache = cache;

    /// <summary>
    /// Stores <paramref name="frame"/> in the cache and returns its canonical anchor_id.
    /// <para>
    /// If an entry already exists for the same anchor_id, its schema is compared to the incoming
    /// schema. A mismatch raises <see cref="NpsAnchorPoisonException"/> (NPS-1 §7.2).
    /// </para>
    /// </summary>
    /// <returns>The canonical <c>sha256:{hex}</c> anchor_id.</returns>
    /// <exception cref="NpsAnchorPoisonException">
    /// Thrown when the incoming schema conflicts with a cached schema for the same anchor_id.
    /// </exception>
    public string Set(AnchorFrame frame)
    {
        var anchorId = frame.AnchorId.StartsWith("sha256:", StringComparison.Ordinal)
            ? frame.AnchorId
            : ComputeAnchorId(frame.Schema);

        // Anchor poisoning check (NPS-1 §7.2):
        // same anchor_id MUST mean identical schema — reject mismatches.
        if (_cache.TryGetValue(anchorId, out AnchorFrame? existing))
        {
            if (!SchemasAreEqual(existing!.Schema, frame.Schema))
                throw new NpsAnchorPoisonException(anchorId);

            // Same schema — idempotent, just refresh TTL by re-setting.
        }

        var options = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromSeconds(frame.Ttl));

        _cache.Set(anchorId, frame, options);
        return anchorId;
    }

    /// <summary>
    /// Attempts to retrieve a cached <see cref="AnchorFrame"/> by <paramref name="anchorId"/>.
    /// Returns <c>false</c> if the entry has expired or was never stored.
    /// </summary>
    public bool TryGet(string anchorId, [NotNullWhen(true)] out AnchorFrame? frame) =>
        _cache.TryGetValue(anchorId, out frame);

    /// <summary>
    /// Returns the cached <see cref="AnchorFrame"/>, or throws <see cref="NpsAnchorNotFoundException"/>.
    /// </summary>
    public AnchorFrame GetRequired(string anchorId) =>
        TryGet(anchorId, out var frame)
            ? frame
            : throw new NpsAnchorNotFoundException(anchorId);

    /// <summary>
    /// Computes a deterministic <c>sha256:{64 lowercase hex chars}</c> anchor_id from
    /// <paramref name="schema"/>. Fields are sorted by name before hashing to ensure
    /// order-independence (NPS-1 §4.1).
    /// </summary>
    public static string ComputeAnchorId(FrameSchema schema)
    {
        // Canonical JSON: fields sorted alphabetically, snake_case keys, no whitespace.
        var normalized = JsonSerializer.Serialize(
            schema.Fields.OrderBy(f => f.Name, StringComparer.Ordinal),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented          = false,
            });

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}"; // full 64-char hex
    }

    // Compares two schemas by re-computing and comparing their canonical anchor_ids.
    private static bool SchemasAreEqual(FrameSchema a, FrameSchema b) =>
        ComputeAnchorId(a) == ComputeAnchorId(b);
}
