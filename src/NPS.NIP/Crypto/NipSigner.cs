// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace NPS.NIP.Crypto;

/// <summary>
/// Ed25519 signing and verification for NIP frames (NPS-3 §4, §5.1).
/// Canonical JSON is produced by serializing properties in alphabetical key order
/// without whitespace (simplified RFC 8785 / JCS for our controlled frame structure).
/// </summary>
public static class NipSigner
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    // ── Sign ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Produces an Ed25519 signature over the canonical JSON of <paramref name="payload"/>
    /// (with the <c>signature</c> field removed / excluded).
    /// Returns the signature as <c>ed25519:{base64url}</c>.
    /// </summary>
    public static string Sign(Key caPrivateKey, object payload)
    {
        var canonical = CanonicalJson(payload);
        var data      = Encoding.UTF8.GetBytes(canonical);
        var sig       = Algorithm.Sign(caPrivateKey, data);
        return "ed25519:" + Base64Url(sig);
    }

    // ── Verify ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies an Ed25519 signature (<c>ed25519:{base64url}</c>) against
    /// the canonical JSON of <paramref name="payload"/> (with the <c>signature</c>
    /// field removed / excluded) using <paramref name="caPubKey"/>.
    /// </summary>
    public static bool Verify(PublicKey caPubKey, object payload, string signature)
    {
        if (!signature.StartsWith("ed25519:", StringComparison.Ordinal)) return false;
        var sigBytes = FromBase64Url(signature["ed25519:".Length..]);

        var canonical = CanonicalJson(payload);
        var data      = Encoding.UTF8.GetBytes(canonical);

        return Algorithm.Verify(caPubKey, data, sigBytes);
    }

    // ── Public key encoding / decoding ────────────────────────────────────────

    /// <summary>Encodes a public key as <c>ed25519:{base64url(raw 32-byte public key)}</c>.</summary>
    public static string EncodePublicKey(PublicKey pubKey) =>
        "ed25519:" + Base64Url(pubKey.Export(KeyBlobFormat.RawPublicKey));

    /// <summary>
    /// Decodes a public key from <c>ed25519:{base64url}</c> format.
    /// Returns null if the format is invalid.
    /// </summary>
    public static PublicKey? DecodePublicKey(string encoded)
    {
        if (!encoded.StartsWith("ed25519:", StringComparison.Ordinal)) return null;
        try
        {
            var raw = FromBase64Url(encoded["ed25519:".Length..]);
            return PublicKey.Import(Algorithm, raw, KeyBlobFormat.RawPublicKey);
        }
        catch { return null; }
    }

    // ── Canonical JSON ────────────────────────────────────────────────────────

    /// <summary>
    /// Produces a canonical JSON string from <paramref name="obj"/> with:
    /// - Properties sorted alphabetically (recursive)
    /// - No whitespace
    /// - <c>signature</c> and <c>metadata</c> keys excluded (not signed per NPS-3 §5.1)
    /// </summary>
    public static string CanonicalJson(object obj)
    {
        // Serialize to JsonDocument first, then re-emit sorted
        var json  = JsonSerializer.Serialize(obj, s_opts);
        using var doc = JsonDocument.Parse(json);
        var sb    = new System.Text.StringBuilder();
        WriteCanonical(doc.RootElement, sb);
        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_opts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Field names excluded from the canonical JSON over which the v1
    /// IdentFrame signature is computed.
    /// <list type="bullet">
    ///   <item><c>signature</c> — the signature itself, by definition.</item>
    ///   <item><c>metadata</c> — informational, not signed (NPS-3 §5.1).</item>
    ///   <item><c>cert_format</c> + <c>cert_chain</c> — NPS-RFC-0002 v2 X.509
    ///     fields. Trust on the v2 path comes from the X.509 chain itself,
    ///     not the v1 CA Ed25519 signature; excluding them here lets v1 and
    ///     v2 frames coexist on the wire (RFC §8.1 Phase 1) without
    ///     re-signing.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> s_excluded = ["signature", "metadata", "cert_format", "cert_chain"];

    private static void WriteCanonical(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var props = el.EnumerateObject()
                    .Where(p => !s_excluded.Contains(p.Name))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                for (int i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(JsonEncodedText.Encode(props[i].Name)).Append("\":");
                    WriteCanonical(props[i].Value, sb);
                }
                sb.Append('}');
                break;

            case JsonValueKind.Array:
                sb.Append('[');
                var items = el.EnumerateArray().ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteCanonical(items[i], sb);
                }
                sb.Append(']');
                break;

            default:
                sb.Append(el.GetRawText());
                break;
        }
    }

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
