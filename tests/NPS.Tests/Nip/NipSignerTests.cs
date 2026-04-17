// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NSec.Cryptography;
using NPS.NIP.Crypto;

namespace NPS.Tests.Nip;

public sealed class NipSignerTests
{
    private static Key NewKey() => Key.Create(SignatureAlgorithm.Ed25519,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    // ── Sign / Verify ─────────────────────────────────────────────────────────

    [Fact]
    public void SignAndVerify_RoundTrip()
    {
        using var key = NewKey();
        var payload = new { frame = "0x20", nid = "urn:nps:agent:ca.test:abc", serial = "0x1" };

        var sig    = NipSigner.Sign(key, payload);
        var result = NipSigner.Verify(key.PublicKey, payload, sig);

        Assert.True(result);
    }

    [Fact]
    public void Verify_WrongKey_ReturnsFalse()
    {
        using var key1 = NewKey();
        using var key2 = NewKey();
        var payload = new { frame = "0x20", nid = "test" };

        var sig    = NipSigner.Sign(key1, payload);
        var result = NipSigner.Verify(key2.PublicKey, payload, sig);

        Assert.False(result);
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsFalse()
    {
        using var key = NewKey();
        var payload1 = new { nid = "urn:nps:agent:ca.test:abc" };
        var payload2 = new { nid = "urn:nps:agent:ca.test:xyz" };   // different

        var sig    = NipSigner.Sign(key, payload1);
        var result = NipSigner.Verify(key.PublicKey, payload2, sig);

        Assert.False(result);
    }

    [Fact]
    public void Verify_InvalidPrefix_ReturnsFalse()
    {
        using var key = NewKey();
        var payload = new { nid = "test" };
        Assert.False(NipSigner.Verify(key.PublicKey, payload, "rsa:invalidsig"));
    }

    // ── Public key encode / decode ────────────────────────────────────────────

    [Fact]
    public void EncodeDecodePublicKey_RoundTrip()
    {
        using var key = NewKey();
        var encoded = NipSigner.EncodePublicKey(key.PublicKey);

        Assert.StartsWith("ed25519:", encoded);

        var decoded = NipSigner.DecodePublicKey(encoded);
        Assert.NotNull(decoded);

        // Same key bytes?
        var orig    = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var decoded2 = decoded!.Export(KeyBlobFormat.RawPublicKey);
        Assert.Equal(orig, decoded2);
    }

    [Fact]
    public void DecodePublicKey_InvalidFormat_ReturnsNull()
    {
        Assert.Null(NipSigner.DecodePublicKey("notakey"));
        Assert.Null(NipSigner.DecodePublicKey("ecdsa:abc"));
        Assert.Null(NipSigner.DecodePublicKey("ed25519:!!!notbase64"));
    }

    // ── Canonical JSON ────────────────────────────────────────────────────────

    [Fact]
    public void CanonicalJson_SortsKeysAlphabetically()
    {
        var payload = new { z_field = "z", a_field = "a", m_field = "m" };
        var json    = NipSigner.CanonicalJson(payload);
        // Keys should appear in alphabetical order
        var aPos = json.IndexOf("a_field", StringComparison.Ordinal);
        var mPos = json.IndexOf("m_field", StringComparison.Ordinal);
        var zPos = json.IndexOf("z_field", StringComparison.Ordinal);
        Assert.True(aPos < mPos && mPos < zPos);
    }

    [Fact]
    public void CanonicalJson_ExcludesSignatureAndMetadata()
    {
        var payload = new { nid = "test", signature = "ed25519:abc", metadata = new { x = 1 } };
        var json    = NipSigner.CanonicalJson(payload);
        Assert.DoesNotContain("signature", json);
        Assert.DoesNotContain("metadata", json);
        Assert.Contains("nid", json);
    }

    [Fact]
    public void CanonicalJson_NoWhitespace()
    {
        var payload = new { a = 1, b = "two" };
        var json    = NipSigner.CanonicalJson(payload);
        Assert.DoesNotContain(" ", json);
        Assert.DoesNotContain("\n", json);
    }

    // ── Base64URL helpers ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 0, 1, 2, 3 })]
    [InlineData(new byte[] { 255, 254, 253 })]
    [InlineData(new byte[] { })]
    public void Base64Url_RoundTrip(byte[] data)
    {
        var encoded = NipSigner.Base64Url(data);
        // No padding, no +/
        Assert.DoesNotContain("=",  encoded);
        Assert.DoesNotContain("+",  encoded);
        Assert.DoesNotContain("/",  encoded);
        var decoded = NipSigner.FromBase64Url(encoded);
        Assert.Equal(data, decoded);
    }
}
