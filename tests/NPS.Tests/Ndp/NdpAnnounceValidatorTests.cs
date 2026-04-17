// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NSec.Cryptography;
using NPS.NDP;
using NPS.NDP.Frames;
using NPS.NDP.Validation;
using NPS.NIP.Crypto;

namespace NPS.Tests.Ndp;

public sealed class NdpAnnounceValidatorTests : IDisposable
{
    private const string NodeNid = "urn:nps:node:api.test:products";

    private readonly Key    _nodeKey;
    private readonly string _nodePubKeyEncoded;

    public NdpAnnounceValidatorTests()
    {
        _nodeKey = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        _nodePubKeyEncoded = NipSigner.EncodePublicKey(_nodeKey.PublicKey);
    }

    public void Dispose() => _nodeKey.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds and signs a valid AnnounceFrame using the node's own private key.
    /// (Mirrors what a real node would do: sign with the key it got its IdentFrame for.)
    /// </summary>
    private AnnounceFrame MakeSignedAnnounce(
        string?   nid      = null,
        uint      ttl      = 300,
        string[]? caps     = null,
        Key?      signingKey = null)
    {
        var actualNid = nid ?? NodeNid;
        var capsList  = (IReadOnlyList<string>)(caps ?? ["nwp:query"]);
        var timestamp = DateTime.UtcNow.ToString("O");
        var addresses = (IReadOnlyList<NdpAddress>)
            [new NdpAddress { Host = "10.0.0.1", Port = 17434, Protocol = "nwp" }];

        // Canonical payload (alphabetical key order, no signature)
        var payload = new
        {
            addresses,
            capabilities = capsList,
            frame        = "0x30",
            nid          = actualNid,
            node_type    = "memory",
            timestamp,
            ttl,
        };

        var key       = signingKey ?? _nodeKey;
        var signature = NipSigner.Sign(key, payload);

        return new AnnounceFrame
        {
            Nid          = actualNid,
            NodeType     = "memory",
            Addresses    = addresses,
            Capabilities = capsList,
            Ttl          = ttl,
            Timestamp    = timestamp,
            Signature    = signature,
        };
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidFrame_Passes()
    {
        var validator = new NdpAnnounceValidator();
        validator.RegisterPublicKey(NodeNid, _nodePubKeyEncoded);
        var frame = MakeSignedAnnounce();

        var result = validator.Validate(frame);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorCode);
    }

    // ── No registered public key ──────────────────────────────────────────────

    [Fact]
    public void Validate_UnknownNid_Fails()
    {
        var validator = new NdpAnnounceValidator(); // no key registered
        var frame     = MakeSignedAnnounce();

        var result = validator.Validate(frame);

        Assert.False(result.IsValid);
        Assert.Equal(NdpErrorCodes.AnnounceNidMismatch, result.ErrorCode);
    }

    // ── Invalid public key encoding ───────────────────────────────────────────

    [Fact]
    public void Validate_BadPublicKeyEncoding_Fails()
    {
        var validator = new NdpAnnounceValidator();
        validator.RegisterPublicKey(NodeNid, "ed25519:!!!notbase64!!!");
        var frame = MakeSignedAnnounce();

        var result = validator.Validate(frame);

        Assert.False(result.IsValid);
        Assert.Equal(NdpErrorCodes.AnnounceSignatureInvalid, result.ErrorCode);
    }

    // ── Wrong signing key ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WrongSigningKey_Fails()
    {
        using var wrongKey = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var validator = new NdpAnnounceValidator();
        // Register the correct public key, but sign the frame with a different key
        validator.RegisterPublicKey(NodeNid, _nodePubKeyEncoded);
        var frame = MakeSignedAnnounce(signingKey: wrongKey);

        var result = validator.Validate(frame);

        Assert.False(result.IsValid);
        Assert.Equal(NdpErrorCodes.AnnounceSignatureInvalid, result.ErrorCode);
    }

    // ── Tampered frame ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_TamperedFrame_Fails()
    {
        var validator = new NdpAnnounceValidator();
        validator.RegisterPublicKey(NodeNid, _nodePubKeyEncoded);

        var frame   = MakeSignedAnnounce();
        var tampered = frame with { Ttl = frame.Ttl + 9999 }; // change after signing

        var result = validator.Validate(tampered);

        Assert.False(result.IsValid);
        Assert.Equal(NdpErrorCodes.AnnounceSignatureInvalid, result.ErrorCode);
    }

    // ── Key registration management ───────────────────────────────────────────

    [Fact]
    public void RegisterPublicKey_Overwrites_OldKey()
    {
        using var newKey = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var validator = new NdpAnnounceValidator();
        validator.RegisterPublicKey(NodeNid, _nodePubKeyEncoded);

        // Replace with a new key, then sign with the new key
        var newPubKeyEncoded = NipSigner.EncodePublicKey(newKey.PublicKey);
        validator.RegisterPublicKey(NodeNid, newPubKeyEncoded);

        var frame  = MakeSignedAnnounce(signingKey: newKey);
        var result = validator.Validate(frame);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void RemovePublicKey_ThenValidate_Fails()
    {
        var validator = new NdpAnnounceValidator();
        validator.RegisterPublicKey(NodeNid, _nodePubKeyEncoded);
        validator.RemovePublicKey(NodeNid);

        var frame  = MakeSignedAnnounce();
        var result = validator.Validate(frame);

        Assert.False(result.IsValid);
        Assert.Equal(NdpErrorCodes.AnnounceNidMismatch, result.ErrorCode);
    }

    [Fact]
    public void KnownPublicKeys_ReflectsRegistrations()
    {
        var validator = new NdpAnnounceValidator();
        Assert.Empty(validator.KnownPublicKeys);

        validator.RegisterPublicKey(NodeNid, _nodePubKeyEncoded);
        Assert.Single(validator.KnownPublicKeys);
        Assert.True(validator.KnownPublicKeys.ContainsKey(NodeNid));
    }
}
