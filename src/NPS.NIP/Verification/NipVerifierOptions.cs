// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;

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

    /// <summary>
    /// Trusted X.509 root certificates for verifying
    /// <see cref="Frames.IdentFrame"/>s with
    /// <see cref="Frames.IdentFrame.CertFormat"/> = <c>"v2-x509"</c>
    /// (NPS-RFC-0002 §4.1). Step 3b chains the leaf up to a root that
    /// matches one of these by Subject DN. Empty / null means the verifier
    /// rejects all v2 chains — which is the safe default while v1 remains
    /// the primary path during Phase 1.
    /// </summary>
    public IReadOnlyList<X509Certificate2>? TrustedX509Roots { get; init; }
}
