// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NPS.NIP;
using NPS.NIP.Ca;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;
using NPS.NIP.Verification;
using NPS.NIP.X509;
using NSec.Cryptography;

namespace NPS.Tests.Nip.X509;

/// <summary>
/// End-to-end tests for the NPS-RFC-0002 X.509 prototype (Phase A).
///
/// <para>The fixture:</para>
/// <list type="bullet">
///   <item>Spins up a CA with an Ed25519 signing key.</item>
///   <item>Self-signs a root cert via <see cref="NipX509Builder.IssueRoot"/>.</item>
///   <item>Issues v2 IdentFrames via <c>NipCaService.RegisterX509Async</c>.</item>
///   <item>Verifies them via <see cref="NipIdentVerifier"/> with both the
///         legacy Ed25519 trust map AND the new X.509 root list populated,
///         exercising the dual-trust path RFC §8.1 Phase 1 mandates.</item>
/// </list>
/// </summary>
public sealed class NipX509Tests : IDisposable
{
    private const string CaNid           = "urn:nps:org:ca.test.example";
    private const string AgentIdentifier = "rfc0002-agent-001";
    private const string AgentNid        = "urn:nps:agent:ca.test.example:rfc0002-agent-001";

    private readonly string             _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly NipKeyManager      _caKeys  = new();
    private readonly NipCaOptions       _opts;
    private readonly InMemoryStore      _store   = new();
    private readonly NipCaService       _svc;
    private readonly Key                _agentKey;
    private readonly X509Certificate2   _rootCert;
    private readonly string             _agentPubKeyEnc;

    public NipX509Tests()
    {
        Directory.CreateDirectory(_tempDir);
        _caKeys.Generate(Path.Combine(_tempDir, "ca.key.enc"), "testpass");

        _opts = new NipCaOptions
        {
            CaNid                 = CaNid,
            KeyFilePath           = Path.Combine(_tempDir, "ca.key.enc"),
            KeyPassphrase         = "testpass",
            BaseUrl               = "https://ca.test.example",
            AgentCertValidityDays = 30,
            NodeCertValidityDays  = 90,
            RenewalWindowDays     = 7,
        };

        _svc = new NipCaService(_opts, _store, _caKeys);

        // Agent's own keypair (separate from the CA).
        _agentKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        _agentPubKeyEnc = NipSigner.EncodePublicKey(_agentKey.PublicKey);

        // Self-signed CA root.
        _rootCert = NipX509Builder.IssueRoot(
            caNid:          CaNid,
            caPrivateKey:   _caKeys.PrivateKey,
            notBefore:      DateTimeOffset.UtcNow.AddMinutes(-1),
            notAfter:       DateTimeOffset.UtcNow.AddYears(5),
            serialNumber:   NewSerial());
    }

    public void Dispose()
    {
        _agentKey.Dispose();
        _caKeys.Dispose();
        _rootCert.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── TC: round-trip ──────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterX509_RoundTrip_VerifierAccepts()
    {
        var frame = await _svc.RegisterX509Async(
            entityType:     "agent",
            identifier:     AgentIdentifier,
            pubKey:         _agentPubKeyEnc,
            capabilities:   ["nwp:query"],
            scopeJson:      """{"nodes":["nwp://api.test.example/*"]}""",
            rootCert:       _rootCert,
            assuranceLevel: AssuranceLevel.Anonymous);

        Assert.Equal(IdentCertFormat.V2X509, frame.CertFormat);
        Assert.NotNull(frame.CertChain);
        Assert.Equal(2, frame.CertChain!.Count);    // leaf + root

        var verifier = BuildVerifier();
        var result   = await verifier.VerifyAsync(frame);
        Assert.True(result.IsValid, $"expected verifier OK; got {result.ErrorCode}: {result.Message}");
    }

    // ── TC: missing-EKU rejection ───────────────────────────────────────────

    [Fact]
    public async Task RegisterX509_LeafEkuStripped_VerifierRejectsCertEkuMissing()
    {
        var frame = await _svc.RegisterX509Async(
            "agent", AgentIdentifier, _agentPubKeyEnc,
            ["nwp:query"], """{"nodes":["nwp://api.test.example/*"]}""",
            _rootCert);

        // Forge a tampered chain: re-issue the leaf without the NPS EKU
        // extension and replace cert_chain[0]. The CA Ed25519 signature on
        // the canonical JSON is unaffected — so step 1-3 still pass; only
        // step 3b (X.509 chain check) catches the missing EKU.
        var tamperedLeaf = ReissueLeafWithoutEku();
        var tamperedFrame = frame with
        {
            CertChain = new string[]
            {
                Base64Url(tamperedLeaf.RawData),
                frame.CertChain![1],
            },
        };

        var result = await BuildVerifier().VerifyAsync(tamperedFrame);
        Assert.False(result.IsValid);
        Assert.Equal(NipErrorCodes.CertEkuMissing, result.ErrorCode);
        tamperedLeaf.Dispose();
    }

    // ── TC: subject / NID mismatch ──────────────────────────────────────────

    [Fact]
    public async Task RegisterX509_LeafForDifferentNid_VerifierRejectsSubjectMismatch()
    {
        var frame = await _svc.RegisterX509Async(
            "agent", AgentIdentifier, _agentPubKeyEnc,
            ["nwp:query"], """{"nodes":["nwp://api.test.example/*"]}""",
            _rootCert);

        // Mint a leaf whose CN/SAN encode a different NID, then splice it in.
        var foreignLeaf = NipX509Builder.IssueLeaf(
            subjectNid:       "urn:nps:agent:ca.test.example:someone-else",
            subjectPubKeyRaw: ExtractRaw(_agentPubKeyEnc),
            caPrivateKey:     _caKeys.PrivateKey,
            issuerNid:        CaNid,
            role:             NipX509Builder.LeafRole.Agent,
            assuranceLevel:   AssuranceLevel.Anonymous,
            notBefore:        DateTimeOffset.UtcNow.AddMinutes(-1),
            notAfter:         DateTimeOffset.UtcNow.AddDays(30),
            serialNumber:     NewSerial());

        var spliced = frame with
        {
            CertChain = new string[]
            {
                Base64Url(foreignLeaf.RawData),
                frame.CertChain![1],
            },
        };

        var result = await BuildVerifier().VerifyAsync(spliced);
        Assert.False(result.IsValid);
        Assert.Equal(NipErrorCodes.CertSubjectNidMismatch, result.ErrorCode);
        foreignLeaf.Dispose();
    }

    // ── TC: cross-format compat ─────────────────────────────────────────────
    //
    // RFC §8.1 Phase 1: v1 + v2 coexist. A v1-only verifier (TrustedX509Roots
    // unset) MUST still accept a v2 frame because the legacy Ed25519 trust
    // path is intact. A v2 frame thus presents to v1 callers as "an
    // IdentFrame with extra fields they ignore".

    [Fact]
    public async Task V1OnlyVerifier_AcceptsV2FrameByIgnoringCertChain()
    {
        var frame = await _svc.RegisterX509Async(
            "agent", AgentIdentifier, _agentPubKeyEnc,
            ["nwp:query"], """{"nodes":["nwp://api.test.example/*"]}""",
            _rootCert);

        // V1-only verifier — TrustedX509Roots intentionally null/empty.
        var v1Verifier = new NipIdentVerifier(new NipVerifierOptions
        {
            TrustedIssuers = new() { [CaNid] = _svc.GetCaPublicKey() },
        });

        var result = await v1Verifier.VerifyAsync(frame);
        Assert.True(result.IsValid,
            $"v1 verifier should accept v2 frames when TrustedX509Roots is null; " +
            $"got {result.ErrorCode}: {result.Message}");
    }

    [Fact]
    public async Task V2Verifier_RejectsV2FrameWhenTrustedRootsMissing()
    {
        var frame = await _svc.RegisterX509Async(
            "agent", AgentIdentifier, _agentPubKeyEnc,
            ["nwp:query"], """{"nodes":["nwp://api.test.example/*"]}""",
            _rootCert);

        // V2-aware verifier with the wrong root list — a different CA's root
        // cert. v1 trust passes (CA pubkey is in TrustedIssuers); v2 chain
        // fails because the leaf's issuer DN doesn't match this root's subject DN.
        using var foreignKeys = new NipKeyManager();
        foreignKeys.Generate(Path.Combine(_tempDir, "foreign.key.enc"), "p");
        using var foreignRoot = NipX509Builder.IssueRoot(
            "urn:nps:org:ca.foreign.example",
            foreignKeys.PrivateKey,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            NewSerial());

        var v2Verifier = new NipIdentVerifier(new NipVerifierOptions
        {
            TrustedIssuers   = new() { [CaNid] = _svc.GetCaPublicKey() },
            TrustedX509Roots = new[] { foreignRoot },
        });

        var result = await v2Verifier.VerifyAsync(frame);
        Assert.False(result.IsValid);
        Assert.Equal(NipErrorCodes.CertFormatInvalid, result.ErrorCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private NipIdentVerifier BuildVerifier()
    {
        return new NipIdentVerifier(new NipVerifierOptions
        {
            TrustedIssuers   = new() { [CaNid] = _svc.GetCaPublicKey() },
            TrustedX509Roots = new[] { _rootCert },
        });
    }

    private X509Certificate2 ReissueLeafWithoutEku()
    {
        // Build a leaf cert WITHOUT the NPS EKU. Subject + SAN still match
        // the AgentNid so the test isolates EKU enforcement specifically.
        var subjectName = new X500DistinguishedName(
            $"CN={AgentNid.Replace(",", "\\,")}");
        var subjectPubKey = Ed25519PublicKey.FromRaw(ExtractRaw(_agentPubKeyEnc));
        var req = new CertificateRequest(subjectName, subjectPubKey, HashAlgorithmName.SHA256);

        // Basic constraints + KeyUsage but NO EKU — exactly the negative case.
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddUri(new Uri(AgentNid));
        req.CertificateExtensions.Add(sanBuilder.Build(false));

        var generator = new Ed25519X509SignatureGenerator(_caKeys.PrivateKey);
        var issuerName = new X500DistinguishedName($"CN={CaNid}");
        return req.Create(
            issuerName, generator,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            NewSerial());
    }

    private static byte[] ExtractRaw(string encoded)
    {
        const string prefix = "ed25519:";
        return NipSigner.FromBase64Url(encoded[prefix.Length..]);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] NewSerial()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        // Force positive integer per X.509 spec.
        bytes[0] &= 0x7F;
        if (bytes[0] == 0) bytes[0] = 0x01;
        return bytes;
    }

    // ── In-memory CA store (mirrors NipCaServiceTests.InMemoryNipCaStore) ───

    private sealed class InMemoryStore : INipCaStore
    {
        private readonly List<NipCertRecord> _records = new();
        private long _serial;

        public Task SaveAsync(NipCertRecord record, CancellationToken ct = default)
        {
            _records.Add(record);
            return Task.CompletedTask;
        }

        public Task<NipCertRecord?> GetByNidAsync(string nid, CancellationToken ct = default) =>
            Task.FromResult<NipCertRecord?>(
                _records.Where(r => r.Nid == nid).MaxBy(r => r.IssuedAt));

        public Task<NipCertRecord?> GetBySerialAsync(string serial, CancellationToken ct = default) =>
            Task.FromResult<NipCertRecord?>(_records.FirstOrDefault(r => r.Serial == serial));

        public Task<bool> RevokeAsync(string nid, string reason, DateTime revokedAt, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<string> NextSerialAsync(CancellationToken ct = default) =>
            Task.FromResult($"0x{Interlocked.Increment(ref _serial):X}");

        public Task<IReadOnlyList<NipCertRecord>> GetRevokedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NipCertRecord>>(Array.Empty<NipCertRecord>());

        public Task<IReadOnlyList<NipCertRecord>> GetByParentNidAsync(string parentNid, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NipCertRecord>>(
                _records.Where(r => r.ParentNid == parentNid).ToList());
    }
}
