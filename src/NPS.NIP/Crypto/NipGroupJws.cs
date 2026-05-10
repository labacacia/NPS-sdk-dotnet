// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using NPS.NIP.Ca;
using NSec.Cryptography;

namespace NPS.NIP.Crypto;

/// <summary>
/// Group-JWS verifier for NPS-CR-0003 §3.5 / §5.1.3 session-issue requests.
/// <para>
/// The flattened JWS shape is:
/// <code>
/// {
///   "protected": "&lt;b64url(header)&gt;",
///   "payload":   "&lt;b64url(payload)&gt;",
///   "signature": "&lt;b64url(Ed25519 signature)&gt;"
/// }
/// </code>
/// where the protected header MUST be <c>{ "alg": "EdDSA",
/// "kid": "&lt;group_nid&gt;", "nps-purpose": "session-issue" }</c>
/// and the signature is computed over the ASCII bytes of
/// <c>protected || "." || payload</c> per RFC 7515 §3.</para>
/// </summary>
public static class NipGroupJws
{
    public const string ExpectedAlg     = "EdDSA";
    public const string ExpectedPurpose = "session-issue";

    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    /// <summary>
    /// Parses + verifies a flattened JWS object (already deserialised to
    /// <paramref name="jws"/>). On success returns the decoded payload as
    /// JSON text and the asserted <c>kid</c>; on failure returns the
    /// matching <see cref="NipErrorCodes"/> code via <paramref name="errorCode"/>.
    /// </summary>
    public static bool TryVerify(
        FlattenedJws  jws,
        PublicKey     groupPubKey,
        out string?   payloadJson,
        out string?   kid,
        out string?   errorCode)
    {
        payloadJson = null;
        kid         = null;

        if (string.IsNullOrEmpty(jws.Protected) ||
            string.IsNullOrEmpty(jws.Payload)   ||
            string.IsNullOrEmpty(jws.Signature))
        {
            errorCode = NipErrorCodes.JwsInvalid;
            return false;
        }

        // Decode + validate the protected header
        byte[] headerBytes;
        byte[] payloadBytes;
        byte[] sigBytes;
        try
        {
            headerBytes  = NipSigner.FromBase64Url(jws.Protected!);
            payloadBytes = NipSigner.FromBase64Url(jws.Payload!);
            sigBytes     = NipSigner.FromBase64Url(jws.Signature!);
        }
        catch
        {
            errorCode = NipErrorCodes.JwsInvalid;
            return false;
        }

        JwsHeader? header;
        try
        {
            header = JsonSerializer.Deserialize<JwsHeader>(headerBytes);
        }
        catch
        {
            errorCode = NipErrorCodes.JwsInvalid;
            return false;
        }
        if (header is null ||
            !ExpectedAlg.Equals(header.Alg, StringComparison.Ordinal) ||
            !ExpectedPurpose.Equals(header.NpsPurpose, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(header.Kid))
        {
            errorCode = NipErrorCodes.JwsInvalid;
            return false;
        }

        // RFC 7515 §3 signing input: ASCII(protected) "." ASCII(payload)
        var signingInput = Encoding.ASCII.GetBytes(jws.Protected + "." + jws.Payload);
        if (!Algorithm.Verify(groupPubKey, signingInput, sigBytes))
        {
            errorCode = NipErrorCodes.JwsInvalid;
            return false;
        }

        kid         = header.Kid;
        payloadJson = Encoding.UTF8.GetString(payloadBytes);
        errorCode   = null;
        return true;
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    /// <summary>Flattened JWS object as it appears on the wire / in JSON.</summary>
    public sealed class FlattenedJws
    {
        public string? Protected { get; set; }
        public string? Payload   { get; set; }
        public string? Signature { get; set; }
    }

    /// <summary>JWS protected header (only the fields NPS uses).</summary>
    private sealed class JwsHeader
    {
        [System.Text.Json.Serialization.JsonPropertyName("alg")]
        public string? Alg { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("kid")]
        public string? Kid { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("nps-purpose")]
        public string? NpsPurpose { get; set; }
    }
}
