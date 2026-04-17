// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Verification;

/// <summary>
/// Configuration for <see cref="NipIdentVerifier"/> — the Node-side identity verifier (NPS-3 §7).
/// </summary>
public sealed class NipVerifierOptions
{
    /// <summary>
    /// Trusted CA issuers, keyed by issuer NID (e.g. <c>urn:nps:org:ca.example.com</c>).
    /// Value is the CA's public key in <c>ed25519:{base64url}</c> format.
    /// Step 2 (trusted-issuer check) and Step 3 (signature verification) use this map.
    /// </summary>
    public required Dictionary<string, string> TrustedIssuers { get; init; }

    /// <summary>
    /// Optional URL of the CA's OCSP endpoint used for real-time revocation check (Step 4).
    /// When null and no <see cref="LocalRevokedSerials"/> are provided, the revocation
    /// step is skipped and a warning is logged.
    /// </summary>
    public string? OcspUrl { get; init; }

    /// <summary>
    /// Optional local set of revoked certificate serial numbers (hex strings, e.g. <c>"0x0A3F9C"</c>).
    /// Checked before making an OCSP network call.
    /// Useful for offline scenarios and unit tests.
    /// </summary>
    public IReadOnlySet<string>? LocalRevokedSerials { get; init; }
}
