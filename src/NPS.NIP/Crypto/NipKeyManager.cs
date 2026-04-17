// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using NSec.Cryptography;

namespace NPS.NIP.Crypto;

/// <summary>
/// Manages the CA Ed25519 keypair: generation, AES-256-GCM encrypted file
/// persistence, and in-memory access (NPS-3 §10.1).
/// <para>
/// Key file format (binary):
/// <c>[12-byte nonce][16-byte tag][N-byte ciphertext]</c>
/// where ciphertext = AES-256-GCM(key=kdf(password), plaintext=raw_private_key_32_bytes).
/// Passphrase is derived to a 256-bit key via PBKDF2-SHA256 (600 000 iterations).
/// </para>
/// </summary>
public sealed class NipKeyManager : IDisposable
{
    private const int NonceSize    = 12;
    private const int TagSize      = 16;
    private const int Pbkdf2Iters  = 600_000;
    private const int RawKeyBytes  = 32;  // Ed25519 raw private key

    private Key?       _privateKey;
    private PublicKey? _publicKey;

    /// <summary>Ed25519 public key of the loaded CA keypair.</summary>
    public PublicKey PublicKey  => _publicKey  ?? throw new InvalidOperationException("CA key not loaded.");

    /// <summary>Ed25519 private key of the loaded CA keypair (in-memory only).</summary>
    public Key PrivateKey => _privateKey ?? throw new InvalidOperationException("CA key not loaded.");

    /// <summary>Whether a keypair is currently loaded.</summary>
    public bool IsLoaded => _privateKey is not null;

    // ── Generate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a new Ed25519 keypair, saves it encrypted to <paramref name="keyFilePath"/>,
    /// and loads it into memory.
    /// </summary>
    public void Generate(string keyFilePath, string passphrase)
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        SaveEncrypted(key, keyFilePath, passphrase);
        Load(keyFilePath, passphrase);
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads and decrypts the CA keypair from <paramref name="keyFilePath"/>.
    /// Throws <see cref="CryptographicException"/> if the passphrase is wrong or file is corrupt.
    /// </summary>
    public void Load(string keyFilePath, string passphrase)
    {
        var fileBytes = File.ReadAllBytes(keyFilePath);

        if (fileBytes.Length < NonceSize + TagSize + RawKeyBytes)
            throw new CryptographicException("CA key file is too short or corrupt.");

        var nonce      = fileBytes[..NonceSize];
        var tag        = fileBytes[NonceSize..(NonceSize + TagSize)];
        var ciphertext = fileBytes[(NonceSize + TagSize)..];
        var aesKey     = DeriveKey(passphrase, nonce);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(aesKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        var key = Key.Import(SignatureAlgorithm.Ed25519, plaintext,
            KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        _privateKey?.Dispose();
        _privateKey = key;
        _publicKey  = key.PublicKey;

        // Scrub plaintext from memory
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(aesKey);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static void SaveEncrypted(Key key, string filePath, string passphrase)
    {
        var raw    = key.Export(KeyBlobFormat.RawPrivateKey);
        var nonce  = RandomNumberGenerator.GetBytes(NonceSize);
        var aesKey = DeriveKey(passphrase, nonce);

        var ciphertext = new byte[raw.Length];
        var tag        = new byte[TagSize];
        using var aes  = new AesGcm(aesKey, TagSize);
        aes.Encrypt(nonce, raw, ciphertext, tag);

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, NonceSize);
        ciphertext.CopyTo(output, NonceSize + TagSize);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(filePath, output);

        CryptographicOperations.ZeroMemory(raw);
        CryptographicOperations.ZeroMemory(aesKey);
    }

    private static byte[] DeriveKey(string passphrase, byte[] nonce)
    {
        // Use first 16 bytes of nonce as PBKDF2 salt (distinct from AES nonce)
        var salt = nonce[..Math.Min(16, nonce.Length)];
        return Rfc2898DeriveBytes.Pbkdf2(
            passphrase, salt,
            Pbkdf2Iters,
            HashAlgorithmName.SHA256,
            outputLength: 32);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _privateKey?.Dispose();
        _privateKey = null;
        _publicKey  = null;
    }
}
