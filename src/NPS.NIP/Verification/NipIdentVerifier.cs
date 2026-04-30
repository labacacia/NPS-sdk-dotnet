// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NPS.NIP.Ca;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;
using NPS.NIP.X509;

namespace NPS.NIP.Verification;

/// <summary>
/// Node-side verifier that implements the full NPS-3 §7 verification flow for an
/// <see cref="IdentFrame"/> received from an Agent.
///
/// <para>The six steps (all MUST pass):</para>
/// <list type="number">
///   <item>Expiry: <c>expires_at &gt; now</c></item>
///   <item>Trusted issuer: <c>issued_by</c> is in <see cref="NipVerifierOptions.TrustedIssuers"/></item>
///   <item>Signature: Ed25519 signature verifies against the issuer CA's public key</item>
///   <item>Revocation: serial not in local CRL; optional OCSP call to CA server</item>
///   <item>Capabilities: frame's capability set contains all <see cref="NipVerifyContext.RequiredCapabilities"/></item>
///   <item>Scope: <c>scope.nodes</c> patterns cover <see cref="NipVerifyContext.TargetNodePath"/></item>
/// </list>
/// </summary>
public sealed class NipIdentVerifier
{
    private readonly NipVerifierOptions        _opts;
    private readonly IHttpClientFactory?       _httpFactory;
    private readonly ILogger<NipIdentVerifier>? _logger;

    public NipIdentVerifier(
        NipVerifierOptions         opts,
        IHttpClientFactory?        httpFactory = null,
        ILogger<NipIdentVerifier>? logger      = null)
    {
        _opts        = opts;
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    /// <summary>
    /// Verifies <paramref name="frame"/> against <paramref name="context"/>.
    /// Steps 1–3 and 5–6 are synchronous; Step 4 (revocation) may make an async OCSP call.
    /// </summary>
    public async Task<NipIdentVerifyResult> VerifyAsync(
        IdentFrame      frame,
        NipVerifyContext? context = null,
        CancellationToken ct     = default)
    {
        context ??= new NipVerifyContext();
        var now   = context.AsOf ?? DateTime.UtcNow;

        // ── Step 1: Expiry ────────────────────────────────────────────────────
        if (!DateTime.TryParse(frame.ExpiresAt, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt)
            || expiresAt <= now)
        {
            return NipIdentVerifyResult.Fail(1,
                NipErrorCodes.CertExpired,
                $"Certificate expired at {frame.ExpiresAt}.");
        }

        // ── Step 2: Trusted issuer ────────────────────────────────────────────
        if (!_opts.TrustedIssuers.TryGetValue(frame.IssuedBy, out var issuerPubKeyEncoded))
        {
            return NipIdentVerifyResult.Fail(2,
                NipErrorCodes.CertUntrusted,
                $"Issuer '{frame.IssuedBy}' is not in the trusted issuers list.");
        }

        // ── Step 3: Signature ─────────────────────────────────────────────────
        var issuerPubKey = NipSigner.DecodePublicKey(issuerPubKeyEncoded);
        if (issuerPubKey is null)
        {
            return NipIdentVerifyResult.Fail(3,
                NipErrorCodes.CertSigInvalid,
                $"Failed to decode public key for issuer '{frame.IssuedBy}'.");
        }

        if (!NipSigner.Verify(issuerPubKey, frame, frame.Signature))
        {
            return NipIdentVerifyResult.Fail(3,
                NipErrorCodes.CertSigInvalid,
                "Certificate signature verification failed.");
        }

        // ── Step 3b: X.509 chain (NPS-RFC-0002, only when cert_format=v2-x509) ─
        // Layered on top of the v1 Ed25519 check rather than replacing it
        // (RFC §8.1 Phase 1: v1 + v2 coexist).
        //
        // <para>A "v1-only" verifier (one that has NOT been configured with
        // X.509 trust anchors) MUST treat a v2 frame as if it were v1 — i.e.
        // ignore cert_chain entirely. That's how a node running pre-RFC-0002
        // code can keep accepting traffic from agents that have already
        // upgraded. We detect this stance by the absence of
        // <see cref="NipVerifierOptions.TrustedX509Roots"/>.</para>
        //
        // <para>A "v2-aware" verifier (TrustedX509Roots populated) runs BOTH
        // the v1 Ed25519 check (above) AND the X.509 chain check (here).</para>
        var trustedX509 = _opts.TrustedX509Roots;
        var hasV2Trust  = trustedX509 is { Count: > 0 };
        if (hasV2Trust &&
            string.Equals(frame.CertFormat, IdentCertFormat.V2X509, StringComparison.Ordinal))
        {
            var x509Result = NipX509Verifier.Verify(
                certChainBase64UrlDer:    frame.CertChain ?? Array.Empty<string>(),
                assertedNid:              frame.Nid,
                assertedAssuranceLevel:   frame.AssuranceLevel,
                trustedRootCerts:         trustedX509!);
            if (!x509Result.Valid)
            {
                return NipIdentVerifyResult.Fail(3,
                    x509Result.ErrorCode ?? NipErrorCodes.CertFormatInvalid,
                    x509Result.Message   ?? "X.509 chain validation failed.");
            }
        }

        // ── Step 4: Revocation ────────────────────────────────────────────────
        var revocationResult = await CheckRevocationAsync(frame, ct);
        if (!revocationResult.IsValid) return revocationResult;

        // ── Step 5: Capabilities ──────────────────────────────────────────────
        if (context.RequiredCapabilities is { Count: > 0 })
        {
            var frameCapSet = frame.Capabilities.ToHashSet(StringComparer.Ordinal);
            var missing = context.RequiredCapabilities
                .Where(c => !frameCapSet.Contains(c))
                .ToList();
            if (missing.Count > 0)
            {
                return NipIdentVerifyResult.Fail(5,
                    NipErrorCodes.CertCapMissing,
                    $"Certificate is missing required capabilities: {string.Join(", ", missing)}.");
            }
        }

        // ── Step 6: Scope ─────────────────────────────────────────────────────
        if (context.TargetNodePath is not null)
        {
            var scopeResult = CheckScope(frame, context.TargetNodePath);
            if (!scopeResult.IsValid) return scopeResult;
        }

        return NipIdentVerifyResult.Ok();
    }

    // ── Revocation (Step 4) ───────────────────────────────────────────────────

    private async Task<NipIdentVerifyResult> CheckRevocationAsync(
        IdentFrame frame, CancellationToken ct)
    {
        // Local CRL check first (fast, no network)
        if (_opts.LocalRevokedSerials?.Contains(frame.Serial) == true)
        {
            return NipIdentVerifyResult.Fail(4,
                NipErrorCodes.CertRevoked,
                $"Certificate serial {frame.Serial} is in the local revocation list.");
        }

        // OCSP call to the CA server (optional)
        if (_opts.OcspUrl is not null && _httpFactory is not null)
        {
            return await OcspCheckAsync(frame.Nid, frame.Serial, ct);
        }

        if (_opts.OcspUrl is not null && _httpFactory is null)
        {
            _logger?.LogWarning(
                "OcspUrl is configured but IHttpClientFactory is not available. " +
                "Skipping revocation check for NID {Nid}.", frame.Nid);
        }
        else if (_opts.OcspUrl is null && (_opts.LocalRevokedSerials is null || _opts.LocalRevokedSerials.Count == 0))
        {
            _logger?.LogDebug(
                "No revocation source configured (OcspUrl and LocalRevokedSerials are both unset). " +
                "Skipping revocation check for NID {Nid}.", frame.Nid);
        }

        return NipIdentVerifyResult.Ok(); // pass-through when revocation is unconfigured
    }

    private async Task<NipIdentVerifyResult> OcspCheckAsync(
        string nid, string serial, CancellationToken ct)
    {
        try
        {
            using var client = _httpFactory!.CreateClient("NipOcsp");
            var url = $"{_opts.OcspUrl!.TrimEnd('/')}/{Uri.EscapeDataString(nid)}";
            var resp = await client.GetAsync(url, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "OCSP endpoint returned {Status} for NID {Nid}. Treating as revoked.",
                    resp.StatusCode, nid);
                return NipIdentVerifyResult.Fail(4,
                    NipErrorCodes.OcspUnavailable,
                    $"OCSP endpoint returned {(int)resp.StatusCode}.");
            }

            using var json = await resp.Content.ReadFromJsonAsync<JsonDocument>(ct);
            var isValid = json?.RootElement
                .TryGetProperty("valid", out var validEl) == true
                && validEl.GetBoolean();

            if (!isValid)
            {
                var errorCode = json?.RootElement
                    .TryGetProperty("error_code", out var ecEl) == true
                    ? ecEl.GetString() ?? NipErrorCodes.CertRevoked
                    : NipErrorCodes.CertRevoked;

                return NipIdentVerifyResult.Fail(4, errorCode,
                    $"OCSP check failed for NID {nid}.");
            }

            return NipIdentVerifyResult.Ok();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogWarning(ex,
                "OCSP call failed for NID {Nid}. Failing open (treating as not revoked).", nid);
            // Fail-open per RFC 6960 §2.4 recommendation when OCSP is unavailable
            return NipIdentVerifyResult.Ok();
        }
    }

    // ── Scope check (Step 6) ──────────────────────────────────────────────────

    private static NipIdentVerifyResult CheckScope(IdentFrame frame, string targetPath)
    {
        if (!frame.Scope.TryGetProperty("nodes", out var nodesEl)
            || nodesEl.ValueKind != JsonValueKind.Array)
        {
            return NipIdentVerifyResult.Fail(6,
                NipErrorCodes.CertScope,
                "IdentFrame scope is missing 'nodes' field.");
        }

        foreach (var pattern in nodesEl.EnumerateArray())
        {
            var p = pattern.GetString();
            if (p is not null && NwpPathMatches(p, targetPath))
                return NipIdentVerifyResult.Ok();
        }

        return NipIdentVerifyResult.Fail(6,
            NipErrorCodes.CertScope,
            $"Target path '{targetPath}' is not covered by the certificate scope.");
    }

    /// <summary>
    /// Matches a NWP path against a scope pattern.
    /// Rules:
    /// <list type="bullet">
    ///   <item>A bare <c>*</c> matches any path.</item>
    ///   <item>A trailing <c>/*</c> (e.g. <c>nwp://api.myapp.com/*</c>) matches the prefix and any path under it.</item>
    ///   <item>All other patterns are exact case-insensitive matches.</item>
    /// </list>
    /// </summary>
    public static bool NwpPathMatches(string pattern, string path)
    {
        if (pattern == "*") return true;

        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2]; // strip "/*"
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                   && (path.Length == prefix.Length
                       || path[prefix.Length] == '/');
        }

        return string.Equals(pattern, path, StringComparison.OrdinalIgnoreCase);
    }
}
