// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP.Ca;

namespace NPS.Tests.Nip;

public sealed class InMemoryNipCaStoreTests
{
    [Fact]
    public async Task List_ReturnsAllRecords()
    {
        var store = new InMemoryNipCaStore();
        await store.SaveAsync(MakeRecord("urn:nps:agent:test.example:a", "0x1"));
        await store.SaveAsync(MakeRecord("urn:nps:agent:test.example:b", "0x2"));

        var records = await store.ListAsync();

        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Serial == "0x1");
        Assert.Contains(records, r => r.Serial == "0x2");
    }

    [Fact]
    public async Task Revoke_MovesRecordIntoCrl()
    {
        var store = new InMemoryNipCaStore();
        await store.SaveAsync(MakeRecord("urn:nps:agent:test.example:a", "0x1"));

        var revoked = await store.RevokeAsync(
            "urn:nps:agent:test.example:a",
            "key_compromise",
            DateTime.UtcNow);

        var crl = await store.GetRevokedAsync();
        Assert.True(revoked);
        Assert.Single(crl);
        Assert.Equal("key_compromise", crl[0].RevokeReason);
    }

    private static NipCertRecord MakeRecord(string nid, string serial) => new()
    {
        Nid          = nid,
        EntityType   = "agent",
        Serial       = serial,
        PubKey       = "ed25519:test",
        Capabilities = ["nwp:query"],
        ScopeJson    = "{}",
        IssuedBy     = "urn:nps:org:ca.test.example",
        IssuedAt     = DateTime.UtcNow,
        ExpiresAt    = DateTime.UtcNow.AddDays(1),
    };
}
