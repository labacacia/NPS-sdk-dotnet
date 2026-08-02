// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP.Orchestration;

/// <summary>
/// NOP v0.7 <c>result_ttl_seconds</c> semantics: how long a completed task's result stays
/// readable. Result-serving surfaces call <see cref="IsExpired"/> before returning a stored
/// record and answer <c>NOP-TASK-RESULT-EXPIRED</c> (→ <c>NPS-CLIENT-NOT-FOUND</c>) when it
/// returns true. Kept as a helper so <c>INopTaskStore</c> implementations stay TTL-agnostic.
/// </summary>
public static class NopResultRetention
{
    /// <summary>
    /// True when <paramref name="record"/> completed longer than its
    /// <c>result_ttl_seconds</c> ago. In-flight tasks (no <see cref="NopTaskRecord.CompletedAt"/>)
    /// never expire.
    /// </summary>
    public static bool IsExpired(NopTaskRecord record, DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.CompletedAt is not { } completedAt)
            return false;
        var now = utcNow ?? DateTime.UtcNow;
        return now >= completedAt.AddSeconds(record.Frame.ResultTtlSeconds);
    }
}
