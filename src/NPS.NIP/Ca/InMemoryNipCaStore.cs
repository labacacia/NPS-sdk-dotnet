// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Ca;

/// <summary>
/// In-memory <see cref="INipCaStore"/> for tests, demos, and single-process
/// development stacks. Not durable; use SQLite or PostgreSQL for production.
/// </summary>
public sealed class InMemoryNipCaStore : INipCaStore
{
    private readonly object _gate = new();
    private readonly List<NipCertRecord> _records = [];
    private long _serial;

    public Task SaveAsync(NipCertRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_records.Any(r => string.Equals(r.Nid, record.Nid, StringComparison.Ordinal)))
                throw new InvalidOperationException($"NID already exists: {record.Nid}");
            if (_records.Any(r => string.Equals(r.Serial, record.Serial, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Serial already exists: {record.Serial}");
            _records.Add(record);
        }
        return Task.CompletedTask;
    }

    public Task<NipCertRecord?> GetByNidAsync(string nid, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_records.LastOrDefault(r => string.Equals(r.Nid, nid, StringComparison.Ordinal)));
    }

    public Task<NipCertRecord?> GetBySerialAsync(string serial, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_records.FirstOrDefault(r => string.Equals(r.Serial, serial, StringComparison.Ordinal)));
    }

    public Task<bool> RevokeAsync(string nid, string reason, DateTime revokedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var idx = _records.FindLastIndex(r =>
                string.Equals(r.Nid, nid, StringComparison.Ordinal) && !r.RevokedAt.HasValue);
            if (idx < 0) return Task.FromResult(false);
            var r = _records[idx];
            _records[idx] = Copy(r, revokedAt, reason);
            return Task.FromResult(true);
        }
    }

    public Task<string> NextSerialAsync(CancellationToken ct = default)
    {
        var next = Interlocked.Increment(ref _serial);
        return Task.FromResult($"0x{next:X}");
    }

    public Task<IReadOnlyList<NipCertRecord>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<NipCertRecord>>(_records.ToList());
    }

    public Task<IReadOnlyList<NipCertRecord>> GetRevokedAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<NipCertRecord>>(
                _records.Where(r => r.RevokedAt.HasValue).ToList());
    }

    public Task<IReadOnlyList<NipCertRecord>> GetByParentNidAsync(string parentNid, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<NipCertRecord>>(
                _records.Where(r => string.Equals(r.ParentNid, parentNid, StringComparison.Ordinal)).ToList());
    }

    private static NipCertRecord Copy(NipCertRecord r, DateTime? revokedAt, string? revokeReason) => new()
    {
        Nid          = r.Nid,
        EntityType   = r.EntityType,
        Serial       = r.Serial,
        PubKey       = r.PubKey,
        Capabilities = r.Capabilities,
        ScopeJson    = r.ScopeJson,
        IssuedBy     = r.IssuedBy,
        IssuedAt     = r.IssuedAt,
        ExpiresAt    = r.ExpiresAt,
        RevokedAt    = revokedAt,
        RevokeReason = revokeReason,
        MetadataJson = r.MetadataJson,
        NidRole      = r.NidRole,
        ParentNid    = r.ParentNid,
        LineageJson  = r.LineageJson,
    };
}
