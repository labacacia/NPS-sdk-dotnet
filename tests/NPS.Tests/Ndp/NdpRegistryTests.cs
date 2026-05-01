// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NDP.Frames;
using NPS.NDP.Registry;

namespace NPS.Tests.Ndp;

public sealed class NdpRegistryTests
{
    private static AnnounceFrame MakeAnnounce(
        string nid,
        string host  = "10.0.0.1",
        int    port  = 17434,
        uint   ttl   = 300) =>
        new()
        {
            Nid          = nid,
            NodeType     = "memory",
            Addresses    = [new NdpAddress { Host = host, Port = port, Protocol = "nwp" }],
            Capabilities = ["nwp:query"],
            Ttl          = ttl,
            Timestamp    = DateTime.UtcNow.ToString("O"),
            Signature    = "ed25519:placeholder",
        };

    // ── Announce / GetByNid ───────────────────────────────────────────────────

    [Fact]
    public void Announce_StoresEntry_GetByNid_Returns()
    {
        var reg   = new InMemoryNdpRegistry();
        var frame = MakeAnnounce("urn:nps:node:api.test:products");
        reg.Announce(frame);

        var result = reg.GetByNid("urn:nps:node:api.test:products");
        Assert.NotNull(result);
        Assert.Equal("urn:nps:node:api.test:products", result!.Nid);
    }

    [Fact]
    public void Announce_TtlZero_EvictsEntry()
    {
        var reg = new InMemoryNdpRegistry();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:orders"));
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:orders", ttl: 0));

        Assert.Null(reg.GetByNid("urn:nps:node:api.test:orders"));
    }

    [Fact]
    public void Announce_Refreshes_ExistingEntry()
    {
        var reg = new InMemoryNdpRegistry();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", host: "10.0.0.1"));
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", host: "10.0.0.2"));

        var result = reg.GetByNid("urn:nps:node:api.test:products");
        Assert.Equal("10.0.0.2", result!.Addresses[0].Host);
    }

    [Fact]
    public void GetByNid_ExpiredEntry_ReturnsNull()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var reg = new InMemoryNdpRegistry { Clock = () => now };

        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", ttl: 60));

        // Advance clock past TTL
        var futureNow = now.AddSeconds(61);
        reg = new InMemoryNdpRegistry { Clock = () => futureNow };
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", ttl: 60));

        // Advance again to expire
        var expiredReg = new InMemoryNdpRegistry { Clock = () => futureNow.AddSeconds(61) };
        expiredReg.Announce(MakeAnnounce("urn:nps:node:api.test:products", ttl: 60));
        // Move clock forward on the same instance
        var clockRef = futureNow.AddSeconds(61);
        var reg2 = new InMemoryNdpRegistry { Clock = () => clockRef };
        reg2.Announce(MakeAnnounce("urn:nps:node:api.test:products", ttl: 60));
        clockRef = clockRef.AddSeconds(61);

        Assert.Null(reg2.GetByNid("urn:nps:node:api.test:products"));
    }

    [Fact]
    public void GetAll_ReturnsAllLiveEntries()
    {
        var reg = new InMemoryNdpRegistry();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products"));
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:orders"));
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:users"));

        var all = reg.GetAll();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void GetAll_ExcludesExpired()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var clock = t;
        var reg = new InMemoryNdpRegistry { Clock = () => clock };

        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", ttl: 300));
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:orders",   ttl: 10));

        clock = t.AddSeconds(15); // products still live, orders expired
        var all = reg.GetAll();

        Assert.Single(all);
        Assert.Equal("urn:nps:node:api.test:products", all[0].Nid);
    }

    // ── Resolve ───────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_KnownTarget_ReturnsEndpoint()
    {
        var reg = new InMemoryNdpRegistry();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", host: "10.0.0.5", port: 17434));

        var result = reg.Resolve("nwp://api.test/products");
        Assert.NotNull(result);
        Assert.Equal("10.0.0.5", result!.Host);
        Assert.Equal(17434, result.Port);
    }

    [Fact]
    public void Resolve_SubPath_ReturnsEndpoint()
    {
        var reg = new InMemoryNdpRegistry();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", host: "10.0.0.5"));

        var result = reg.Resolve("nwp://api.test/products/123");
        Assert.NotNull(result);
    }

    [Fact]
    public void Resolve_UnknownTarget_ReturnsNull()
    {
        var reg = new InMemoryNdpRegistry();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products"));

        var result = reg.Resolve("nwp://api.test/inventory");
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_AfterEviction_ReturnsNull()
    {
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var reg   = new InMemoryNdpRegistry { Clock = () => clock };
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", ttl: 30));

        clock = clock.AddSeconds(31);
        var result = reg.Resolve("nwp://api.test/products");
        Assert.Null(result);
    }

    // ── Thread safety (smoke) ─────────────────────────────────────────────────

    [Fact]
    public void ConcurrentAnnounce_DoesNotThrow()
    {
        var reg = new InMemoryNdpRegistry();
        var tasks = Enumerable.Range(0, 50).Select(i =>
            Task.Run(() => reg.Announce(
                MakeAnnounce($"urn:nps:node:api.test:node-{i % 5}")))).ToArray();
        Task.WaitAll(tasks);
        Assert.True(reg.GetAll().Count <= 5);
    }

    // ── ParseNpsTxtRecord ─────────────────────────────────────────────────────

    [Fact]
    public void ParseNpsTxtRecord_ValidRecord_ReturnsResult()
    {
        const string txt = "v=nps1 type=memory port=17434 nid=urn:nps:node:api.example.com:products";
        var result = InMemoryNdpRegistry.ParseNpsTxtRecord(txt, "api.example.com");

        Assert.NotNull(result);
        Assert.Equal("api.example.com", result!.Host);
        Assert.Equal(17434, result.Port);
        Assert.Equal(300u, result.Ttl);
        Assert.Null(result.CertFingerprint);
    }

    [Fact]
    public void ParseNpsTxtRecord_MissingV_ReturnsNull()
    {
        const string txt = "type=memory port=17434 nid=urn:nps:node:api.example.com:products";
        Assert.Null(InMemoryNdpRegistry.ParseNpsTxtRecord(txt, "api.example.com"));
    }

    [Fact]
    public void ParseNpsTxtRecord_WrongV_ReturnsNull()
    {
        const string txt = "v=nps2 nid=urn:nps:node:api.example.com:products";
        Assert.Null(InMemoryNdpRegistry.ParseNpsTxtRecord(txt, "api.example.com"));
    }

    [Fact]
    public void ParseNpsTxtRecord_MissingNid_ReturnsNull()
    {
        const string txt = "v=nps1 type=memory port=17434";
        Assert.Null(InMemoryNdpRegistry.ParseNpsTxtRecord(txt, "api.example.com"));
    }

    [Fact]
    public void ParseNpsTxtRecord_DefaultPort()
    {
        const string txt = "v=nps1 nid=urn:nps:node:api.example.com:products";
        var result = InMemoryNdpRegistry.ParseNpsTxtRecord(txt, "api.example.com");

        Assert.NotNull(result);
        Assert.Equal(17433, result!.Port);
    }

    [Fact]
    public void ParseNpsTxtRecord_WithFingerprint()
    {
        const string txt = "v=nps1 nid=urn:nps:node:api.example.com:products fp=sha256:a3f9deadbeef";
        var result = InMemoryNdpRegistry.ParseNpsTxtRecord(txt, "api.example.com");

        Assert.NotNull(result);
        Assert.Equal("sha256:a3f9deadbeef", result!.CertFingerprint);
    }

    // ── ResolveViaDns ─────────────────────────────────────────────────────────

    private sealed class FakeDnsTxtLookup : IDnsTxtLookup
    {
        private readonly IReadOnlyList<string> _records;
        public int CallCount { get; private set; }

        public FakeDnsTxtLookup(params string[] records) =>
            _records = records;

        public IReadOnlyList<string> Lookup(string hostname)
        {
            CallCount++;
            return _records;
        }
    }

    [Fact]
    public void ResolveViaDns_RegistryHit_NoDnsCall()
    {
        var reg = new InMemoryNdpRegistry();
        reg.Announce(MakeAnnounce("urn:nps:node:api.test:products", host: "10.0.0.5", port: 17434));

        var fake   = new FakeDnsTxtLookup("v=nps1 nid=urn:nps:node:api.test:products");
        var result = reg.ResolveViaDns("nwp://api.test/products", fake);

        Assert.NotNull(result);
        Assert.Equal(0, fake.CallCount); // registry hit; DNS must not be called
    }

    [Fact]
    public void ResolveViaDns_RegistryMiss_UsesDns()
    {
        var reg  = new InMemoryNdpRegistry(); // empty registry
        const string txt = "v=nps1 nid=urn:nps:node:api.test:products port=17434";
        var fake = new FakeDnsTxtLookup(txt);

        var result = reg.ResolveViaDns("nwp://api.test/products", fake);

        Assert.NotNull(result);
        Assert.Equal(1, fake.CallCount);
        Assert.Equal("api.test", result!.Host);
        Assert.Equal(17434, result.Port);
    }

    [Fact]
    public void ResolveViaDns_InvalidTxt_ReturnsNull()
    {
        var reg  = new InMemoryNdpRegistry();
        // TXT record missing required "v" key
        var fake = new FakeDnsTxtLookup("type=memory port=17434 nid=urn:nps:node:api.test:products");

        var result = reg.ResolveViaDns("nwp://api.test/products", fake);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveViaDns_EmptyDnsResponse_ReturnsNull()
    {
        var reg  = new InMemoryNdpRegistry();
        var fake = new FakeDnsTxtLookup(); // returns no records

        var result = reg.ResolveViaDns("nwp://api.test/products", fake);

        Assert.Null(result);
        Assert.Equal(1, fake.CallCount);
    }
}
