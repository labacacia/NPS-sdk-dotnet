// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NIP.Ca;
using NPS.NIP.Crypto;

namespace NPS.Tests.Nip;

public sealed class NipCaServiceTests : IDisposable
{
    private readonly string       _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly NipKeyManager _keys;
    private readonly NipCaOptions  _opts;
    private readonly INipCaStore   _store;
    private readonly NipCaService  _svc;

    public NipCaServiceTests()
    {
        Directory.CreateDirectory(_tempDir);

        _keys = new NipKeyManager();
        _keys.Generate(Path.Combine(_tempDir, "ca.key.enc"), "testpass");

        _opts = new NipCaOptions
        {
            CaNid            = "urn:nps:org:ca.test.example",
            KeyFilePath      = Path.Combine(_tempDir, "ca.key.enc"),
            KeyPassphrase    = "testpass",
            BaseUrl          = "https://ca.test.example",
            ConnectionString = "unused-in-tests",
            AgentCertValidityDays = 30,
            NodeCertValidityDays  = 90,
            RenewalWindowDays     = 7,
        };

        _store = new InMemoryNipCaStore();
        _svc   = new NipCaService(_opts, _store, _keys);
    }

    public void Dispose()
    {
        _keys.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Register ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_ReturnsValidIdentFrame()
    {
        var frame = await _svc.RegisterAsync(
            "agent", "test-agent-001",
            "ed25519:MCowBQYDK2VwAyEAplaceholderkey123456789012345678901234==",
            ["nwp:query", "nwp:stream"],
            """{"nodes":["nwp://api.test.example/*"]}""");

        Assert.Equal("urn:nps:agent:ca.test.example:test-agent-001", frame.Nid);
        Assert.Equal("urn:nps:org:ca.test.example", frame.IssuedBy);
        Assert.Contains("nwp:query", frame.Capabilities);
        Assert.False(string.IsNullOrEmpty(frame.Serial));
        Assert.False(string.IsNullOrEmpty(frame.Signature));
        Assert.StartsWith("ed25519:", frame.Signature);
    }

    [Fact]
    public async Task Register_NidBuildCorrectly()
    {
        var frame = await _svc.RegisterAsync("agent", "my-agent", "ed25519:abc",
            [], "{}");
        Assert.Equal("urn:nps:agent:ca.test.example:my-agent", frame.Nid);
    }

    [Fact]
    public async Task Register_DuplicateNid_Throws()
    {
        await _svc.RegisterAsync("agent", "dupe", "ed25519:abc", [], "{}");
        var ex = await Assert.ThrowsAsync<NipCaException>(() =>
            _svc.RegisterAsync("agent", "dupe", "ed25519:abc", [], "{}"));
        Assert.Equal(NipErrorCodes.NidAlreadyExists, ex.ErrorCode);
    }

    [Fact]
    public async Task Register_PersistsToStore()
    {
        await _svc.RegisterAsync("agent", "persisted", "ed25519:abc", [], "{}");
        var record = await _store.GetByNidAsync("urn:nps:agent:ca.test.example:persisted");
        Assert.NotNull(record);
        Assert.Equal("agent", record!.EntityType);
    }

    [Fact]
    public async Task Register_AgentExpiry_Is30Days()
    {
        var before = DateTime.UtcNow;
        var frame  = await _svc.RegisterAsync("agent", "expiry-check", "ed25519:abc", [], "{}");
        var expires = DateTime.Parse(frame.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(expires >= before.AddDays(29).AddHours(23));
        Assert.True(expires <= before.AddDays(30).AddSeconds(5));
    }

    [Fact]
    public async Task Register_NodeExpiry_Is90Days()
    {
        var before = DateTime.UtcNow;
        var frame  = await _svc.RegisterAsync("node", "my-node", "ed25519:abc", [], "{}");
        var expires = DateTime.Parse(frame.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(expires >= before.AddDays(89).AddHours(23));
    }

    // ── Revoke ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoke_ReturnsRevokeFrame()
    {
        await _svc.RegisterAsync("agent", "to-revoke", "ed25519:abc", [], "{}");
        var nid   = "urn:nps:agent:ca.test.example:to-revoke";
        var frame = await _svc.RevokeAsync(nid, "key_compromise");

        Assert.Equal(nid, frame.TargetNid);
        Assert.Equal("key_compromise", frame.Reason);
        Assert.StartsWith("ed25519:", frame.Signature);
    }

    [Fact]
    public async Task Revoke_NotFound_Throws()
    {
        var ex = await Assert.ThrowsAsync<NipCaException>(() =>
            _svc.RevokeAsync("urn:nps:agent:ca.test.example:ghost", "superseded"));
        Assert.Equal(NipErrorCodes.NidNotFound, ex.ErrorCode);
    }

    // ── Verify ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Verify_ValidCert_ReturnsValid()
    {
        await _svc.RegisterAsync("agent", "to-verify", "ed25519:abc", [], "{}");
        var result = await _svc.VerifyAsync("urn:nps:agent:ca.test.example:to-verify");
        Assert.True(result.Valid);
        Assert.NotNull(result.Record);
    }

    [Fact]
    public async Task Verify_RevokedCert_ReturnsInvalid()
    {
        await _svc.RegisterAsync("agent", "to-verify-rev", "ed25519:abc", [], "{}");
        var nid = "urn:nps:agent:ca.test.example:to-verify-rev";
        await _svc.RevokeAsync(nid, "superseded");

        var result = await _svc.VerifyAsync(nid);
        Assert.False(result.Valid);
        Assert.Equal(NipErrorCodes.CertRevoked, result.ErrorCode);
    }

    [Fact]
    public async Task Verify_NotFound_ReturnsInvalid()
    {
        var result = await _svc.VerifyAsync("urn:nps:agent:ca.test.example:ghost");
        Assert.False(result.Valid);
        Assert.Equal(NipErrorCodes.NidNotFound, result.ErrorCode);
    }

    // ── Renew ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Renew_OutsideWindow_Throws()
    {
        await _svc.RegisterAsync("agent", "too-early", "ed25519:abc", [], "{}");
        // Cert is fresh — renewal window not open yet
        var ex = await Assert.ThrowsAsync<NipCaException>(() =>
            _svc.RenewAsync("urn:nps:agent:ca.test.example:too-early"));
        Assert.Equal(NipErrorCodes.RenewalTooEarly, ex.ErrorCode);
    }

    // ── CRL ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCrl_IncludesRevoked()
    {
        await _svc.RegisterAsync("agent", "crl-test", "ed25519:abc", [], "{}");
        await _svc.RevokeAsync("urn:nps:agent:ca.test.example:crl-test", "superseded");

        var crl = await _svc.GetCrlAsync();
        Assert.Contains(crl, r => r.Nid == "urn:nps:agent:ca.test.example:crl-test");
    }

    // ── CA public key ─────────────────────────────────────────────────────────

    [Fact]
    public void GetCaPublicKey_ReturnsEd25519Format()
    {
        var pk = _svc.GetCaPublicKey();
        Assert.StartsWith("ed25519:", pk);
        Assert.True(pk.Length > 20);
    }
}

// ── In-memory store for unit tests ────────────────────────────────────────────

file sealed class InMemoryNipCaStore : INipCaStore
{
    private readonly List<NipCertRecord> _records = [];
    private long _serial = 0;

    public Task SaveAsync(NipCertRecord record, CancellationToken ct = default)
    {
        _records.Add(record);
        return Task.CompletedTask;
    }

    public Task<NipCertRecord?> GetByNidAsync(string nid, CancellationToken ct = default) =>
        Task.FromResult<NipCertRecord?>(_records.Where(r => r.Nid == nid).MaxBy(r => r.IssuedAt));

    public Task<NipCertRecord?> GetBySerialAsync(string serial, CancellationToken ct = default) =>
        Task.FromResult<NipCertRecord?>(_records.FirstOrDefault(r => r.Serial == serial));

    public Task<bool> RevokeAsync(string nid, string reason, DateTime revokedAt, CancellationToken ct = default)
    {
        var idx = _records.FindLastIndex(r => r.Nid == nid && !r.RevokedAt.HasValue);
        if (idx < 0) return Task.FromResult(false);
        var r = _records[idx];
        _records[idx] = new NipCertRecord
        {
            Nid          = r.Nid,          EntityType   = r.EntityType,
            Serial       = r.Serial,       PubKey       = r.PubKey,
            Capabilities = r.Capabilities, ScopeJson    = r.ScopeJson,
            IssuedBy     = r.IssuedBy,     IssuedAt     = r.IssuedAt,
            ExpiresAt    = r.ExpiresAt,    RevokedAt    = revokedAt,
            RevokeReason = reason,         MetadataJson = r.MetadataJson,
        };
        return Task.FromResult(true);
    }

    public Task<string> NextSerialAsync(CancellationToken ct = default) =>
        Task.FromResult($"0x{Interlocked.Increment(ref _serial):X}");

    public Task<IReadOnlyList<NipCertRecord>> GetRevokedAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NipCertRecord>>(
            _records.Where(r => r.RevokedAt.HasValue).ToList());
}
