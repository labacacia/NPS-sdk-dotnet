// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Ca.Ra;

/// <summary>
/// Enrollment Tier 3: every inbound registration request is queued as a
/// <see cref="PendingRegistration"/>. The CA replies <c>202 Accepted</c>
/// with a <c>pending_id</c>; an operator must approve or reject via the
/// management endpoints (NPS-CR-0005 §3.4).
/// </summary>
public sealed class PendingQueuePolicy : IEnrollmentPolicy
{
    private readonly IPendingStore _store;
    private readonly int           _maxSize;

    public PendingQueuePolicy(IPendingStore store, int maxSize)
    {
        _store   = store;
        _maxSize = maxSize;
    }

    public async Task CheckAsync(
        string entityType, string identifier, string pubKey,
        IReadOnlyList<string> capabilities, string scopeJson, string? metadataJson,
        string? enrollmentToken, CancellationToken ct)
    {
        if (_store.PendingCount >= _maxSize)
            throw new NipCaException(
                $"Pending enrollment queue is full (max {_maxSize}). Retry later.",
                NipErrorCodes.RaTokenInvalid);

        var id  = Guid.NewGuid().ToString("N");
        var req = new PendingRegistration(
            id, entityType, identifier, pubKey,
            capabilities, scopeJson, metadataJson,
            DateTimeOffset.UtcNow, PendingStatus.Pending, null);

        await _store.EnqueueAsync(req, ct);
        throw new NipRaPendingException(id);
    }
}
