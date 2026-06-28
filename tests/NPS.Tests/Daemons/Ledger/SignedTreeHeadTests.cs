// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Daemon.Ledger;
using NPS.NIP.Crypto;
using NSec.Cryptography;

namespace NPS.Tests.Daemons.Ledger;

/// <summary>
/// Round-trip tests for STH signing — proving the ed25519 signature over
/// the canonical-with-signature-excluded form verifies, and that mutating
/// any STH field invalidates the signature. This is the contract clients
/// rely on when pinning the operator key.
/// </summary>
public sealed class SignedTreeHeadTests
{
    private static (Key Priv, PublicKey Pub) NewKey()
    {
        var k = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (k, k.PublicKey);
    }

    private static SignedTreeHead Sign(Key priv, SignedTreeHead unsigned)
    {
        var sig = NipSigner.Sign(priv, unsigned with { Signature = string.Empty });
        return unsigned with { Signature = sig };
    }

    [Fact]
    public void Roundtrip_ValidSignatureVerifies()
    {
        using var lease = LeasedKey();
        var (priv, pub) = lease;
        var sth = new SignedTreeHead
        {
            LogId          = "urn:nps:node:log.local:operator-test",
            TreeSize       = 7,
            Timestamp      = "2026-04-30T00:00:00Z",
            Sha256RootHash = "abc123def456",
            Signature      = string.Empty,
        };

        var signed = Sign(priv, sth);

        Assert.True(NipSigner.Verify(
            pub, signed with { Signature = string.Empty }, signed.Signature));
    }

    [Fact]
    public void TamperedRoot_FailsVerification()
    {
        using var lease = LeasedKey();
        var (priv, pub) = lease;
        var sth = new SignedTreeHead
        {
            LogId          = "urn:nps:node:log.local:operator-test",
            TreeSize       = 7,
            Timestamp      = "2026-04-30T00:00:00Z",
            Sha256RootHash = "abc123def456",
            Signature      = string.Empty,
        };

        var signed   = Sign(priv, sth);
        var tampered = signed with { Sha256RootHash = "ffffffffffffffff" };

        Assert.False(NipSigner.Verify(
            pub, tampered with { Signature = string.Empty }, tampered.Signature));
    }

    [Fact]
    public void TamperedTreeSize_FailsVerification()
    {
        using var lease = LeasedKey();
        var (priv, pub) = lease;
        var sth = new SignedTreeHead
        {
            LogId          = "urn:nps:node:log.local:operator-test",
            TreeSize       = 7,
            Timestamp      = "2026-04-30T00:00:00Z",
            Sha256RootHash = "abc123def456",
            Signature      = string.Empty,
        };

        var signed   = Sign(priv, sth);
        var tampered = signed with { TreeSize = 8 };

        Assert.False(NipSigner.Verify(
            pub, tampered with { Signature = string.Empty }, tampered.Signature));
    }

    [Fact]
    public void DifferentLogId_FailsVerificationUnderSameKey()
    {
        // Cross-log replay protection: an STH from log "A" must not verify
        // under log "B" even if the signing key happens to be the same.
        using var lease = LeasedKey();
        var (priv, pub) = lease;
        var sthA = new SignedTreeHead
        {
            LogId          = "urn:nps:node:log.local:operator-A",
            TreeSize       = 1,
            Timestamp      = "2026-04-30T00:00:00Z",
            Sha256RootHash = "abc",
            Signature      = string.Empty,
        };

        var signed = Sign(priv, sthA);

        var asLogB = signed with { LogId = "urn:nps:node:log.local:operator-B" };
        Assert.False(NipSigner.Verify(
            pub, asLogB with { Signature = string.Empty }, asLogB.Signature));
    }

    private static KeyLease LeasedKey()
    {
        var (priv, pub) = NewKey();
        return new KeyLease(priv, pub);
    }

    private sealed class KeyLease : IDisposable
    {
        public Key Priv { get; }
        public PublicKey Pub { get; }
        public KeyLease(Key priv, PublicKey pub) { Priv = priv; Pub = pub; }
        public void Dispose() => Priv.Dispose();
        public void Deconstruct(out Key priv, out PublicKey pub) { priv = Priv; pub = Pub; }
    }
}
