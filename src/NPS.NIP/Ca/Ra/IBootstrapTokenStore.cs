// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Ca.Ra;

/// <summary>
/// Persistent store for single-use enrollment bootstrap tokens (NPS-CR-0005 §3.3).
/// Tokens are stored as SHA-256 hashes; the raw token value is returned only once
/// at creation time and never persisted.
/// </summary>
public interface IBootstrapTokenStore
{
    /// <summary>
    /// Creates a new bootstrap token, stores its hash, and returns the raw token
    /// value (prefixed <c>nps-bootstrap-</c>). Caller is responsible for
    /// delivering it out-of-band to the enrolling entity.
    /// </summary>
    Task<string> CreateAsync(string? label, DateTimeOffset expiresAt, CancellationToken ct);

    /// <summary>
    /// Validates <paramref name="token"/> against stored hashes and, if valid
    /// and unexpired, marks it as consumed (one-shot). Returns <c>false</c>
    /// if the token was not found, already consumed, or expired.
    /// </summary>
    Task<bool> ValidateAndConsumeAsync(string token, CancellationToken ct);

    /// <summary>Returns all tokens (consumed or live) for operator inspection.</summary>
    Task<IReadOnlyList<BootstrapTokenInfo>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Administratively revokes a token before it is consumed. Returns
    /// <c>false</c> if the ID was not found or already consumed.
    /// </summary>
    Task<bool> RevokeAsync(string tokenId, CancellationToken ct);
}

/// <summary>Public metadata for a bootstrap token (token value excluded).</summary>
public sealed record BootstrapTokenInfo(
    string         Id,
    string?        Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool           Consumed,
    bool           Revoked);
