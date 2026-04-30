// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NSec.Cryptography;

// Disambiguate `PublicKey` — both NSec.Cryptography and
// System.Security.Cryptography.X509Certificates define one and we need both.
using X509PublicKey = System.Security.Cryptography.X509Certificates.PublicKey;
using NSecPublicKey = NSec.Cryptography.PublicKey;

namespace NPS.NIP.X509;

/// <summary>
/// <see cref="X509SignatureGenerator"/> implementation for Ed25519 using NSec
/// (RFC 8410 — Ed25519 in X.509). .NET's built-in
/// <see cref="CertificateRequest"/> does not natively sign with Ed25519, so
/// the prototype hands it this generator at <c>Create(...)</c> time. The
/// <c>hashAlgorithm</c> argument is ignored — Ed25519 hashes the message
/// internally with SHA-512 and there is no caller-provided digest.
/// </summary>
public sealed class Ed25519X509SignatureGenerator : X509SignatureGenerator
{
    private readonly Key    _caPrivateKey;
    private readonly byte[] _caPubKeyRaw;     // 32-byte raw Ed25519 pubkey

    public Ed25519X509SignatureGenerator(Key caPrivateKey)
    {
        if (caPrivateKey.Algorithm != SignatureAlgorithm.Ed25519)
            throw new ArgumentException("caPrivateKey must be an Ed25519 key.", nameof(caPrivateKey));
        _caPrivateKey = caPrivateKey;
        _caPubKeyRaw  = caPrivateKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public override byte[] GetSignatureAlgorithmIdentifier(HashAlgorithmName hashAlgorithm)
    {
        // RFC 8410 §3 — Ed25519 AlgorithmIdentifier: SEQUENCE { OID 1.3.101.112 } with no parameters.
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(NpsX509Oids.Ed25519);
        }
        return w.Encode();
    }

    public override byte[] SignData(byte[] data, HashAlgorithmName hashAlgorithm) =>
        // Ed25519 signs the raw message; hashAlgorithm is ignored.
        SignatureAlgorithm.Ed25519.Sign(_caPrivateKey, data);

    protected override X509PublicKey BuildPublicKey() =>
        Ed25519PublicKey.FromRaw(_caPubKeyRaw);
}

/// <summary>
/// Helpers for encoding / decoding Ed25519 public keys as
/// <c>SubjectPublicKeyInfo</c> per RFC 8410.
/// </summary>
public static class Ed25519PublicKey
{
    /// <summary>
    /// Wraps a raw 32-byte Ed25519 pubkey as a .NET X.509
    /// <see cref="X509PublicKey"/> via DER-encoded
    /// <c>SubjectPublicKeyInfo</c>.
    /// </summary>
    public static X509PublicKey FromRaw(ReadOnlySpan<byte> rawPubKey32)
    {
        if (rawPubKey32.Length != 32)
            throw new ArgumentException("Ed25519 raw public key must be 32 bytes.", nameof(rawPubKey32));

        // SubjectPublicKeyInfo ::= SEQUENCE {
        //   algorithm   AlgorithmIdentifier (Ed25519),
        //   subjectPublicKey BIT STRING (raw 32 bytes)
        // }
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(NpsX509Oids.Ed25519);
            }
            w.WriteBitString(rawPubKey32);
        }
        var spki = w.Encode();
        return X509PublicKey.CreateFromSubjectPublicKeyInfo(spki, out _);
    }

    /// <summary>
    /// Extracts the raw 32-byte Ed25519 pubkey from a .NET X.509
    /// <see cref="X509PublicKey"/>'s SubjectPublicKeyInfo encoding.
    /// Returns <c>null</c> if the SPKI is not Ed25519-shaped.
    /// </summary>
    public static byte[]? ExtractRaw(X509PublicKey publicKey)
    {
        if (publicKey.Oid?.Value != NpsX509Oids.Ed25519) return null;
        try
        {
            var spki = publicKey.ExportSubjectPublicKeyInfo();
            var reader = new AsnReader(spki, AsnEncodingRules.DER);
            var spkiSeq = reader.ReadSequence();
            spkiSeq.ReadSequence();   // skip AlgorithmIdentifier
            var bitString = spkiSeq.ReadBitString(out _);
            return bitString.Length == 32 ? bitString : null;
        }
        catch { return null; }
    }
}
