// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Ca.Ra;

/// <summary>
/// In-memory implementation of <see cref="IPendingStore"/>.
/// Suitable for single-node deployments and integration tests.
/// A background sweep removes records older than
/// <see cref="NipCaOptions.PendingQueueMaxAge"/> to bound memory growth.
/// </summary>
public sealed class InMemoryPendingStore : IPendingStore, IDisposable
{
    private readonly SemaphoreSlim                       _lock    = new(1, 1);
    private readonly Dictionary<string, PendingRegistration> _records = [];
    private readonly TimeSpan                            _maxAge;
    private readonly Timer                               _sweep;

    public InMemoryPendingStore(TimeSpan maxAge)
    {
        _maxAge = maxAge;
        _sweep  = new Timer(Sweep, null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
    }

    public int PendingCount
    {
        get
        {
            _lock.Wait();
            try { return _records.Values.Count(r => r.Status == PendingStatus.Pending); }
            finally { _lock.Release(); }
        }
    }

    public async Task<string> EnqueueAsync(PendingRegistration request, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { _records[request.Id] = request; }
        finally { _lock.Release(); }
        return request.Id;
    }

    public async Task<IReadOnlyList<PendingRegistration>> ListAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { return [.. _records.Values]; }
        finally { _lock.Release(); }
    }

    public async Task<PendingRegistration?> GetAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { return _records.GetValueOrDefault(id); }
        finally { _lock.Release(); }
    }

    public async Task<bool> ApproveAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_records.TryGetValue(id, out var r) || r.Status != PendingStatus.Pending)
                return false;
            _records[id] = r with { Status = PendingStatus.Approved };
            return true;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> RejectAsync(string id, string reason, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_records.TryGetValue(id, out var r) || r.Status != PendingStatus.Pending)
                return false;
            _records[id] = r with { Status = PendingStatus.Rejected, RejectReason = reason };
            return true;
        }
        finally { _lock.Release(); }
    }

    private void Sweep(object? _)
    {
        var cutoff = DateTimeOffset.UtcNow - _maxAge;
        _lock.Wait();
        try
        {
            var expired = _records.Values
                .Where(r => r.Status != PendingStatus.Pending && r.RequestedAt < cutoff)
                .Select(r => r.Id).ToList();
            foreach (var id in expired) _records.Remove(id);
        }
        finally { _lock.Release(); }
    }

    public void Dispose() => _sweep.Dispose();
}
