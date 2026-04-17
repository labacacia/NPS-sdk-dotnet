// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NSec.Cryptography;
using NPS.NIP.Crypto;

namespace NPS.Tests.Nip;

public sealed class NipKeyManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public NipKeyManagerTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string KeyPath(string name = "ca.key.enc") => Path.Combine(_tempDir, name);

    // ── Generate ──────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_CreatesEncryptedFile()
    {
        using var km = new NipKeyManager();
        km.Generate(KeyPath(), "testpass");

        Assert.True(File.Exists(KeyPath()));
        Assert.True(km.IsLoaded);
    }

    [Fact]
    public void Generate_PublicKeyIsAccessible()
    {
        using var km = new NipKeyManager();
        km.Generate(KeyPath(), "testpass");

        Assert.NotNull(km.PublicKey);
        var raw = km.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        Assert.Equal(32, raw.Length);   // Ed25519 public key = 32 bytes
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_ReadsBackSamePublicKey()
    {
        using var km1 = new NipKeyManager();
        km1.Generate(KeyPath(), "testpass");
        var pubBefore = km1.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        using var km2 = new NipKeyManager();
        km2.Load(KeyPath(), "testpass");
        var pubAfter = km2.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        Assert.Equal(pubBefore, pubAfter);
    }

    [Fact]
    public void Load_WrongPassphrase_Throws()
    {
        using var km1 = new NipKeyManager();
        km1.Generate(KeyPath(), "correctpass");

        using var km2 = new NipKeyManager();
        Assert.ThrowsAny<Exception>(() => km2.Load(KeyPath(), "wrongpass"));
    }

    [Fact]
    public void Load_FileNotFound_Throws()
    {
        using var km = new NipKeyManager();
        Assert.ThrowsAny<Exception>(() => km.Load(KeyPath("missing.key"), "pass"));
    }

    [Fact]
    public void Load_CorruptFile_Throws()
    {
        File.WriteAllBytes(KeyPath(), [0x00, 0x01, 0x02]);  // too short
        using var km = new NipKeyManager();
        Assert.ThrowsAny<Exception>(() => km.Load(KeyPath(), "pass"));
    }

    // ── Signing still works after reload ─────────────────────────────────────

    [Fact]
    public void SignatureAfterReload_VerifiesWithOriginalKey()
    {
        using var km1 = new NipKeyManager();
        km1.Generate(KeyPath(), "pass123");
        var pubKey = km1.PublicKey;

        using var km2 = new NipKeyManager();
        km2.Load(KeyPath(), "pass123");

        var payload = new { nid = "urn:nps:agent:ca.test:reload" };
        var sig     = NipSigner.Sign(km2.PrivateKey, payload);

        Assert.True(NipSigner.Verify(pubKey, payload, sig));
    }

    // ── IsLoaded ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsLoaded_FalseBeforeLoad()
    {
        using var km = new NipKeyManager();
        Assert.False(km.IsLoaded);
    }

    [Fact]
    public void PrivateKey_BeforeLoad_Throws()
    {
        using var km = new NipKeyManager();
        Assert.Throws<InvalidOperationException>(() => _ = km.PrivateKey);
    }
}
