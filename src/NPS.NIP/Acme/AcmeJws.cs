// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using NPS.NIP.Crypto;
using NSec.Cryptography;

namespace NPS.NIP.Acme;

/// <summary>
/// Minimal JWS (RFC 7515) helpers tuned for the NPS-RFC-0002 ACME
/// prototype. Signing/verification is Ed25519-only ("EdDSA" alg per RFC
/// 8037); flattened JWS JSON serialization is used per RFC 8555 §6.2. The
/// client and server share these helpers so wire format details can't drift.
/// </summary>
public static class AcmeJws
{
    /// <summary>JOSE alg identifier for Ed25519 — RFC 8037 §3.1.</summary>
    public const string AlgEdDSA = "EdDSA";

    /// <summary>JOSE kty for Ed25519 — RFC 8037 §2.</summary>
    public const string KtyOKP = "OKP";

    /// <summary>JOSE crv for Ed25519 — RFC 8037 §2.</summary>
    public const string CrvEd25519 = "Ed25519";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Sign ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Signs an ACME request body. The wire shape is the
    /// <see cref="AcmeJwsEnvelope"/> with three base64url members:
    /// <c>protected</c>, <c>payload</c>, <c>signature</c> (RFC 8555 §6.2).
    /// </summary>
    /// <param name="privateKey">Account or agent Ed25519 private key.</param>
    /// <param name="protectedHeader">
    /// JOSE protected header. Must include <c>alg</c>, <c>nonce</c>, <c>url</c>,
    /// and exactly one of <c>jwk</c> (newAccount) or <c>kid</c> (post-account).
    /// </param>
    /// <param name="payload">
    /// The request body. JSON-serialized then base64url-encoded. Pass an
    /// empty object for "POST-as-GET" requests where body is intentionally
    /// empty per RFC 8555 §6.3.
    /// </param>
    public static AcmeJwsEnvelope Sign(Key privateKey, AcmeProtectedHeader protectedHeader, object? payload)
    {
        var protectedJson    = JsonSerializer.Serialize(protectedHeader, JsonOpts);
        var protectedB64Url  = NipSigner.Base64Url(Encoding.UTF8.GetBytes(protectedJson));
        var payloadJson      = payload is null ? string.Empty : JsonSerializer.Serialize(payload, JsonOpts);
        var payloadB64Url    = payload is null ? string.Empty : NipSigner.Base64Url(Encoding.UTF8.GetBytes(payloadJson));

        var signingInput = Encoding.ASCII.GetBytes($"{protectedB64Url}.{payloadB64Url}");
        var signature    = SignatureAlgorithm.Ed25519.Sign(privateKey, signingInput);

        return new AcmeJwsEnvelope(
            ProtectedHeader: protectedB64Url,
            Payload:         payloadB64Url,
            Signature:       NipSigner.Base64Url(signature));
    }

    // ── Verify ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Server-side JWS verification. Returns the parsed
    /// <see cref="AcmeProtectedHeader"/> and the raw payload bytes on
    /// success; throws <see cref="AcmeJwsException"/> on any failure.
    /// </summary>
    public static (AcmeProtectedHeader header, byte[] payloadBytes) Verify(
        AcmeJwsEnvelope envelope, PublicKey publicKey)
    {
        var headerJson = Encoding.UTF8.GetString(NipSigner.FromBase64Url(envelope.ProtectedHeader));
        var header     = JsonSerializer.Deserialize<AcmeProtectedHeader>(headerJson, JsonOpts)
            ?? throw new AcmeJwsException("protected header could not be parsed.");

        if (header.Alg != AlgEdDSA)
            throw new AcmeJwsException($"unsupported alg '{header.Alg}'; only EdDSA is allowed.");

        var signingInput = Encoding.ASCII.GetBytes($"{envelope.ProtectedHeader}.{envelope.Payload}");
        var sigBytes     = NipSigner.FromBase64Url(envelope.Signature);

        if (!SignatureAlgorithm.Ed25519.Verify(publicKey, signingInput, sigBytes))
            throw new AcmeJwsException("signature verification failed.");

        var payloadBytes = envelope.Payload.Length == 0
            ? Array.Empty<byte>()
            : NipSigner.FromBase64Url(envelope.Payload);
        return (header, payloadBytes);
    }

    // ── JWK helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an Ed25519 JWK (RFC 8037 §2) for use in the
    /// <c>jwk</c> protected header on <c>newAccount</c> requests.
    /// </summary>
    public static AcmeJwk Ed25519Jwk(PublicKey publicKey)
    {
        var raw = publicKey.Export(KeyBlobFormat.RawPublicKey);
        return new AcmeJwk(Kty: KtyOKP, Crv: CrvEd25519, X: NipSigner.Base64Url(raw));
    }

    /// <summary>Re-imports an Ed25519 JWK back to <see cref="PublicKey"/>.</summary>
    public static PublicKey ImportJwk(AcmeJwk jwk)
    {
        if (jwk.Kty != KtyOKP || jwk.Crv != CrvEd25519)
            throw new AcmeJwsException($"unsupported JWK kty='{jwk.Kty}' crv='{jwk.Crv}'.");
        var raw = NipSigner.FromBase64Url(jwk.X);
        if (raw.Length != 32)
            throw new AcmeJwsException("Ed25519 JWK x value must be 32 bytes.");
        return PublicKey.Import(SignatureAlgorithm.Ed25519, raw, KeyBlobFormat.RawPublicKey);
    }

    /// <summary>
    /// Computes the canonical JWK thumbprint per RFC 7638 §3 — used to
    /// derive a stable account identifier from the Ed25519 public key.
    /// </summary>
    public static string Thumbprint(AcmeJwk jwk)
    {
        // Canonical JSON form for Ed25519 JWKs: members in lex order, no
        // whitespace, only required fields (kty, crv, x).
        var canonical = $"{{\"crv\":\"{jwk.Crv}\",\"kty\":\"{jwk.Kty}\",\"x\":\"{jwk.X}\"}}";
        var hash      = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return NipSigner.Base64Url(hash);
    }
}

/// <summary>RFC 8555 §6.2 flattened JWS envelope.</summary>
public sealed record AcmeJwsEnvelope(
    [property: System.Text.Json.Serialization.JsonPropertyName("protected")] string ProtectedHeader,
    [property: System.Text.Json.Serialization.JsonPropertyName("payload")]   string Payload,
    [property: System.Text.Json.Serialization.JsonPropertyName("signature")] string Signature);

/// <summary>JOSE protected header for ACME requests (RFC 8555 §6.2).</summary>
public sealed record AcmeProtectedHeader
{
    [System.Text.Json.Serialization.JsonPropertyName("alg")]
    public string Alg { get; init; } = AcmeJws.AlgEdDSA;

    [System.Text.Json.Serialization.JsonPropertyName("nonce")]
    public required string Nonce { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>JWK on <c>newAccount</c>; <c>kid</c> on every other request.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("jwk")]
    public AcmeJwk? Jwk { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("kid")]
    public string? Kid { get; init; }
}

/// <summary>Ed25519 JWK shape (RFC 8037 §2).</summary>
public sealed record AcmeJwk(
    [property: System.Text.Json.Serialization.JsonPropertyName("kty")] string Kty,
    [property: System.Text.Json.Serialization.JsonPropertyName("crv")] string Crv,
    [property: System.Text.Json.Serialization.JsonPropertyName("x")]   string X);

/// <summary>JWS validation failure — caught by the ACME server middleware.</summary>
public sealed class AcmeJwsException : Exception
{
    public AcmeJwsException(string message) : base(message) { }
}
