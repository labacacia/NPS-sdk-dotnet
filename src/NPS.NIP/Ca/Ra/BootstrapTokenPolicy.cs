// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Ca.Ra;

/// <summary>
/// Enrollment Tier 2: caller must present a valid single-use bootstrap token
/// obtained via <c>POST /v1/enrollment/tokens</c> (NPS-CR-0005 §3.3).
/// The token is consumed atomically on success.
/// </summary>
public sealed class BootstrapTokenPolicy : IEnrollmentPolicy
{
    private readonly IBootstrapTokenStore _store;

    public BootstrapTokenPolicy(IBootstrapTokenStore store) => _store = store;

    public async Task CheckAsync(
        string entityType, string identifier, string pubKey,
        IReadOnlyList<string> capabilities, string scopeJson, string? metadataJson,
        string? enrollmentToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(enrollmentToken) ||
            !enrollmentToken.StartsWith("nps-bootstrap-", StringComparison.Ordinal))
            throw new NipCaException(
                "A bootstrap token (prefix 'nps-bootstrap-') is required for enrollment.",
                NipErrorCodes.RaTokenInvalid);

        var valid = await _store.ValidateAndConsumeAsync(enrollmentToken, ct);
        if (!valid)
            throw new NipCaException(
                "Bootstrap token is invalid, expired, or already consumed.",
                NipErrorCodes.RaTokenExpired);
    }
}
