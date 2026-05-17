// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;

namespace NPS.NIP.Ca.Ra;

/// <summary>
/// In-memory implementation of <see cref="IBootstrapTokenStore"/>.
/// Suitable for single-node deployments and integration tests.
/// Not durable across process restarts — use the PostgreSQL implementation
/// (nip-ca-server) for production.
/// </summary>
public sealed class InMemoryBootstrapTokenStore : IBootstrapTokenStore
{
    private sealed record Entry(
        string Id, byte[] Hash, string? Label,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
        bool Consumed, bool Revoked);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<Entry>   _tokens = [];

    public async Task<string> CreateAsync(string? label, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var raw = "nps-bootstrap-" +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var id   = Guid.NewGuid().ToString("N");

        await _lock.WaitAsync(ct);
        try { _tokens.Add(new Entry(id, hash, label, DateTimeOffset.UtcNow, expiresAt, false, false)); }
        finally { _lock.Release(); }

        return raw;
    }

    public async Task<bool> ValidateAndConsumeAsync(string token, CancellationToken ct)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        await _lock.WaitAsync(ct);
        try
        {
            for (var i = 0; i < _tokens.Count; i++)
            {
                var e = _tokens[i];
                if (e.Consumed || e.Revoked) continue;
                if (DateTimeOffset.UtcNow > e.ExpiresAt) continue;
                if (!CryptographicOperations.FixedTimeEquals(hash, e.Hash)) continue;
                _tokens[i] = e with { Consumed = true };
                return true;
            }
            return false;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<BootstrapTokenInfo>> ListAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _tokens.Select(e => new BootstrapTokenInfo(
                e.Id, e.Label, e.CreatedAt, e.ExpiresAt, e.Consumed, e.Revoked)).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> RevokeAsync(string tokenId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            for (var i = 0; i < _tokens.Count; i++)
            {
                var e = _tokens[i];
                if (!string.Equals(e.Id, tokenId, StringComparison.Ordinal)) continue;
                if (e.Consumed || e.Revoked) return false;
                _tokens[i] = e with { Revoked = true };
                return true;
            }
            return false;
        }
        finally { _lock.Release(); }
    }
}
