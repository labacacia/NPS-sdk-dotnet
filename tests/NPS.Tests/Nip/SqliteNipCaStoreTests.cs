// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP.Ca;
using NPS.NIP.Storage;

namespace NPS.Tests.Nip;

/// <summary>
/// Integration tests for <see cref="SqliteNipCaStore"/> — verifies all six
/// <see cref="INipCaStore"/> operations against a real SQLite database.
/// </summary>
public sealed class SqliteNipCaStoreTests : IAsyncLifetime
{
    private string         _dbPath = "";
    private SqliteNipCaStore _store = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"nip_test_{Guid.NewGuid():N}.db");
        _store  = await SqliteNipCaStore.OpenAsync($"Data Source={_dbPath}");
    }

    public Task DisposeAsync()
    {
        try { File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    // ── SaveAsync / GetByNidAsync ─────────────────────────────────────────────

    [Fact]
    public async Task SaveAndGetByNid_RoundTrips()
    {
        var rec = MakeRecord("urn:nps:agent:test:agent-1", "agent", "0x1");
        await _store.SaveAsync(rec);

        var got = await _store.GetByNidAsync("urn:nps:agent:test:agent-1");
        Assert.NotNull(got);
        Assert.Equal(rec.Nid,          got!.Nid);
        Assert.Equal(rec.EntityType,   got.EntityType);
        Assert.Equal(rec.Serial,       got.Serial);
        Assert.Equal(rec.PubKey,       got.PubKey);
        Assert.Equal(rec.Capabilities, got.Capabilities);
        Assert.Equal(rec.ScopeJson,    got.ScopeJson);
        Assert.Equal(rec.IssuedBy,     got.IssuedBy);
        Assert.Equal(rec.IssuedAt,     got.IssuedAt,  TimeSpan.FromSeconds(1));
        Assert.Equal(rec.ExpiresAt,    got.ExpiresAt, TimeSpan.FromSeconds(1));
        Assert.Null(got.RevokedAt);
        Assert.Null(got.RevokeReason);
    }

    [Fact]
    public async Task GetByNid_NotFound_ReturnsNull()
    {
        var got = await _store.GetByNidAsync("urn:nps:agent:test:ghost");
        Assert.Null(got);
    }

    [Fact]
    public async Task GetByNid_MultipleRecords_ReturnsLatest()
    {
        var nid  = "urn:nps:agent:test:renewed";
        var old  = MakeRecord(nid, "agent", "0x1", issuedAt: DateTime.UtcNow.AddDays(-60));
        var fresh= MakeRecord(nid, "agent", "0x2", issuedAt: DateTime.UtcNow);
        await _store.SaveAsync(old);
        await _store.SaveAsync(fresh);

        var got = await _store.GetByNidAsync(nid);
        Assert.Equal("0x2", got!.Serial);
    }

    // ── GetBySerialAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetBySerial_RoundTrips()
    {
        var rec = MakeRecord("urn:nps:agent:test:agent-s", "agent", "0xABCD");
        await _store.SaveAsync(rec);

        var got = await _store.GetBySerialAsync("0xABCD");
        Assert.NotNull(got);
        Assert.Equal("urn:nps:agent:test:agent-s", got!.Nid);
    }

    [Fact]
    public async Task GetBySerial_NotFound_ReturnsNull()
    {
        Assert.Null(await _store.GetBySerialAsync("0xDEAD"));
    }

    // ── RevokeAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoke_SetsRevokedFields()
    {
        var rec = MakeRecord("urn:nps:agent:test:to-revoke", "agent", "0x2");
        await _store.SaveAsync(rec);

        var revokedAt = DateTime.UtcNow;
        var ok = await _store.RevokeAsync("urn:nps:agent:test:to-revoke", "key_compromise", revokedAt);
        Assert.True(ok);

        var got = await _store.GetByNidAsync("urn:nps:agent:test:to-revoke");
        Assert.NotNull(got!.RevokedAt);
        Assert.Equal("key_compromise", got.RevokeReason);
    }

    [Fact]
    public async Task Revoke_NotFound_ReturnsFalse()
    {
        var ok = await _store.RevokeAsync("urn:nps:agent:test:ghost", "superseded", DateTime.UtcNow);
        Assert.False(ok);
    }

    [Fact]
    public async Task Revoke_AlreadyRevoked_ReturnsFalse()
    {
        var rec = MakeRecord("urn:nps:agent:test:already-revoked", "agent", "0x3");
        await _store.SaveAsync(rec);
        await _store.RevokeAsync("urn:nps:agent:test:already-revoked", "superseded", DateTime.UtcNow);

        var ok = await _store.RevokeAsync("urn:nps:agent:test:already-revoked", "key_compromise", DateTime.UtcNow);
        Assert.False(ok);
    }

    // ── NextSerialAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task NextSerial_StartsAtOne()
    {
        var s = await _store.NextSerialAsync();
        Assert.Equal("0x1", s);
    }

    [Fact]
    public async Task NextSerial_Increments()
    {
        var s1 = await _store.NextSerialAsync();
        var s2 = await _store.NextSerialAsync();
        var s3 = await _store.NextSerialAsync();
        Assert.Equal("0x1", s1);
        Assert.Equal("0x2", s2);
        Assert.Equal("0x3", s3);
    }

    // ── GetRevokedAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetRevoked_ReturnsOnlyRevokedRecords()
    {
        await _store.SaveAsync(MakeRecord("urn:nps:agent:test:active",   "agent", "0x10"));
        await _store.SaveAsync(MakeRecord("urn:nps:agent:test:revoked1", "agent", "0x11"));
        await _store.SaveAsync(MakeRecord("urn:nps:agent:test:revoked2", "agent", "0x12"));

        await _store.RevokeAsync("urn:nps:agent:test:revoked1", "superseded", DateTime.UtcNow);
        await _store.RevokeAsync("urn:nps:agent:test:revoked2", "key_compromise", DateTime.UtcNow);

        var crl = await _store.GetRevokedAsync();
        Assert.Equal(2, crl.Count);
        Assert.All(crl, r => Assert.NotNull(r.RevokedAt));
        Assert.DoesNotContain(crl, r => r.Nid == "urn:nps:agent:test:active");
    }

    [Fact]
    public async Task GetRevoked_EmptyStore_ReturnsEmpty()
    {
        var crl = await _store.GetRevokedAsync();
        Assert.Empty(crl);
    }

    [Fact]
    public async Task List_ReturnsAllRecords()
    {
        await _store.SaveAsync(MakeRecord("urn:nps:agent:sqlite.test:a", "agent", "0xA1"));
        await _store.SaveAsync(MakeRecord("urn:nps:agent:sqlite.test:b", "agent", "0xA2"));

        var records = await _store.ListAsync();

        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Serial == "0xA1");
        Assert.Contains(records, r => r.Serial == "0xA2");
    }

    // ── Persistence across open ───────────────────────────────────────────────

    [Fact]
    public async Task Data_PersistedAcrossStoreInstances()
    {
        var rec = MakeRecord("urn:nps:agent:test:persist", "agent", "0x99");
        await _store.SaveAsync(rec);

        // Open a second store instance on the same file
        var store2 = await SqliteNipCaStore.OpenAsync($"Data Source={_dbPath}");
        var got    = await store2.GetByNidAsync("urn:nps:agent:test:persist");
        Assert.NotNull(got);
        Assert.Equal("0x99", got!.Serial);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static NipCertRecord MakeRecord(
        string nid, string entityType, string serial,
        DateTime? issuedAt = null) => new()
    {
        Nid          = nid,
        EntityType   = entityType,
        Serial       = serial,
        PubKey       = "ed25519:abc123",
        Capabilities = ["nwp:query", "nwp:stream"],
        ScopeJson    = """{"nodes":["*"]}""",
        IssuedBy     = "urn:nps:org:ca.test",
        IssuedAt     = issuedAt ?? DateTime.UtcNow,
        ExpiresAt    = (issuedAt ?? DateTime.UtcNow).AddDays(30),
    };
}
