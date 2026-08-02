// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NPS.Daemon.Registry;
using NPS.NDP;
using NPS.NDP.Frames;

namespace NPS.Tests.Daemons.Registry;

/// <summary>
/// nps-registry HTTP surface for NPS-CR-0009 multi-Anchor HA: <c>/v1/cluster/resolve</c>
/// (highest <c>cluster_epoch</c> wins, equal-epoch → <c>NDP-CLUSTER-SPLIT</c> / 409) and the
/// NDP §9 federation endpoints (higher epoch from a peer is preferred, lower is never applied,
/// <c>ndp-forwarded-by</c> loop + 3-hop rules).
/// </summary>
public sealed class RegistryClusterEndpointTests
{
    private const string Cluster = "urn:nps:cluster:api.test:main";
    private const string AnchorA = "urn:nps:node:api.test:anchor-a";
    private const string AnchorB = "urn:nps:node:api.test:anchor-b";
    private const string OwnNid  = "urn:nps:agent:registry-a.test:r1";
    private const string PeerNid = "urn:nps:agent:registry-b.test:r2";

    // ── Fixture ───────────────────────────────────────────────────────────────

    private sealed class Fixture : IAsyncDisposable
    {
        public WebApplication App    { get; }
        public HttpClient     Client { get; }

        private Fixture(WebApplication app, HttpClient client) { App = app; Client = client; }

        public static async Task<Fixture> CreateAsync(string profile = RegistryOptions.ProfilePublicFederated)
        {
            var opts    = new RegistryOptions { Nid = OwnNid, Profile = profile, SqlitePath = null };
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            RegistryHost.WireServices(builder.Services, opts);
            var app = builder.Build();
            RegistryHost.WireRoutes(app, opts);

            await app.StartAsync();
            return new Fixture(app, app.GetTestClient());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try { await App.StopAsync(); } catch { /* ignore */ }
            await App.DisposeAsync();
        }
    }

    private static AnnounceFrame Member(string nid, ulong? epoch, uint ttl = 300) => new()
    {
        Nid           = nid,
        NodeType      = "anchor",
        NodeRoles     = ["anchor"],
        ClusterAnchor = Cluster,
        ClusterEpoch  = epoch,
        Addresses     = [new NdpAddress { Host = "10.0.0.1", Port = 17433, Protocol = "nwp" }],
        Capabilities  = ["topology.read"],
        Ttl           = ttl,
        Timestamp     = DateTime.UtcNow.ToString("O"),
        Signature     = "ed25519:placeholder",
    };

    private static async Task<JsonElement> JsonOf(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static HttpRequestMessage Forwarded(string path, object body, params string[] hops)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (hops.Length > 0)
            req.Headers.TryAddWithoutValidation(NdpFederation.ForwardedByHeader, string.Join(", ", hops));
        return req;
    }

    // ── Announce ingestion (CR-0009 §3.4, backward compatibility §5) ───────────

    [Fact]
    public async Task Announce_PersistsClusterEpoch()
    {
        await using var fx = await Fixture.CreateAsync();

        var res  = await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorB, epoch: 3));
        var body = await JsonOf(res);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("registered", body.GetProperty("status").GetString());
        Assert.Equal(Cluster, body.GetProperty("cluster_anchor").GetString());
        Assert.Equal(3UL, body.GetProperty("cluster_epoch").GetUInt64());
    }

    [Fact]
    public async Task Announce_WithoutClusterEpoch_DefaultsToOne()
    {
        await using var fx = await Fixture.CreateAsync();

        var res  = await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorA, epoch: null));
        var body = await JsonOf(res);

        Assert.Equal(1UL, body.GetProperty("cluster_epoch").GetUInt64());

        var resolved = await JsonOf(await fx.Client.GetAsync($"/v1/cluster/resolve?cluster_anchor={Cluster}"));
        Assert.Equal(AnchorA, resolved.GetProperty("active_nid").GetString());
        Assert.Equal(1UL, resolved.GetProperty("cluster_epoch").GetUInt64());
    }

    // ── /v1/cluster/resolve ───────────────────────────────────────────────────

    [Fact]
    public async Task ClusterResolve_ReturnsHighestEpochAnchor()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorA, epoch: 1));
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorB, epoch: 4));

        var res  = await fx.Client.GetAsync($"/v1/cluster/resolve?cluster_anchor={Cluster}");
        var body = await JsonOf(res);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(AnchorB, body.GetProperty("active_nid").GetString());
        Assert.Equal(4UL, body.GetProperty("cluster_epoch").GetUInt64());
        Assert.Equal("local", body.GetProperty("source").GetString());
        Assert.Equal(17433, body.GetProperty("resolved").GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task ClusterResolve_StaleLowerEpochAnnounce_DoesNotDisplaceActiveAnchor()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorB, epoch: 4));
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorA, epoch: 2)); // superseded leader

        var body = await JsonOf(await fx.Client.GetAsync($"/v1/cluster/resolve?cluster_anchor={Cluster}"));

        Assert.Equal(AnchorB, body.GetProperty("active_nid").GetString());
        Assert.Equal(4UL, body.GetProperty("cluster_epoch").GetUInt64());
    }

    [Fact]
    public async Task ClusterResolve_EqualEpoch_Returns409ClusterSplit()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorA, epoch: 2));
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorB, epoch: 2));

        var res  = await fx.Client.GetAsync($"/v1/cluster/resolve?cluster_anchor={Cluster}");
        var body = await JsonOf(res);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);   // NPS-CLIENT-CONFLICT → 409
        Assert.Equal(NdpErrorCodes.ClusterSplit, body.GetProperty("error").GetString());
        Assert.Equal("NPS-CLIENT-CONFLICT", body.GetProperty("status").GetString());
        Assert.Equal(2UL, body.GetProperty("cluster_epoch").GetUInt64());
    }

    [Fact]
    public async Task ClusterResolve_UnknownCluster_Returns404()
    {
        await using var fx = await Fixture.CreateAsync();

        var res  = await fx.Client.GetAsync($"/v1/cluster/resolve?cluster_anchor={Cluster}");
        var body = await JsonOf(res);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(NdpErrorCodes.ResolveNotFound, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ClusterResolve_MissingParameter_Returns400()
    {
        await using var fx = await Fixture.CreateAsync();
        var res = await fx.Client.GetAsync("/v1/cluster/resolve");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── Federation (NDP §9) ───────────────────────────────────────────────────

    [Fact]
    public async Task Federation_HigherEpochFromPeer_IsPreferred()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorA, epoch: 2));

        var res = await fx.Client.SendAsync(Forwarded("/v1/federation/cluster", new
        {
            cluster_anchor = Cluster,
            cluster_epoch  = 6,
            active_nid     = AnchorB,
        }, PeerNid));
        var body = await JsonOf(res);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("applied", body.GetProperty("status").GetString());
        Assert.Equal(PeerNid, body.GetProperty("source").GetString());

        var resolved = await JsonOf(await fx.Client.GetAsync($"/v1/cluster/resolve?cluster_anchor={Cluster}"));
        Assert.Equal(AnchorB, resolved.GetProperty("active_nid").GetString());
        Assert.Equal(6UL, resolved.GetProperty("cluster_epoch").GetUInt64());
        Assert.Equal(PeerNid, resolved.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Federation_LowerEpochFromPeer_IsIgnored()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorB, epoch: 5));

        var body = await JsonOf(await fx.Client.SendAsync(Forwarded("/v1/federation/cluster", new
        {
            cluster_anchor = Cluster,
            cluster_epoch  = 3,
            active_nid     = AnchorA,
        }, PeerNid)));

        Assert.Equal("ignored", body.GetProperty("status").GetString());
        Assert.Equal(5UL, body.GetProperty("cluster_epoch").GetUInt64());
        Assert.Equal(AnchorB, body.GetProperty("active_nid").GetString());
    }

    [Fact]
    public async Task Federation_EqualEpochDifferentAnchor_Returns409ClusterSplit()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorA, epoch: 3));

        var res  = await fx.Client.SendAsync(Forwarded("/v1/federation/cluster", new
        {
            cluster_anchor = Cluster,
            cluster_epoch  = 3,
            active_nid     = AnchorB,
        }, PeerNid));
        var body = await JsonOf(res);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(NdpErrorCodes.ClusterSplit, body.GetProperty("error").GetString());
        Assert.Equal("NPS-CLIENT-CONFLICT", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Federation_TupleWithoutEpoch_DefaultsToOne()
    {
        await using var fx = await Fixture.CreateAsync();

        var body = await JsonOf(await fx.Client.SendAsync(Forwarded("/v1/federation/cluster", new
        {
            cluster_anchor = Cluster,
            active_nid     = AnchorA,
        }, PeerNid)));

        Assert.Equal("applied", body.GetProperty("status").GetString());
        Assert.Equal(1UL, body.GetProperty("cluster_epoch").GetUInt64());
    }

    [Fact]
    public async Task Federation_LoopInForwardedBy_Returns409FederationLoop()
    {
        await using var fx = await Fixture.CreateAsync();

        var res  = await fx.Client.SendAsync(Forwarded("/v1/federation/cluster", new
        {
            cluster_anchor = Cluster,
            cluster_epoch  = 9,
            active_nid     = AnchorB,
        }, PeerNid, OwnNid));
        var body = await JsonOf(res);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(NdpErrorCodes.FederationLoop, body.GetProperty("error").GetString());

        var resolve = await fx.Client.GetAsync($"/v1/cluster/resolve?cluster_anchor={Cluster}");
        Assert.Equal(HttpStatusCode.NotFound, resolve.StatusCode); // nothing was stored
    }

    [Fact]
    public async Task Federation_BeyondThreeHops_IsSilentlyDropped()
    {
        await using var fx = await Fixture.CreateAsync();

        var res = await fx.Client.SendAsync(Forwarded("/v1/federation/cluster", new
        {
            cluster_anchor = Cluster,
            cluster_epoch  = 9,
            active_nid     = AnchorB,
        }, "urn:nps:agent:r:1", "urn:nps:agent:r:2", "urn:nps:agent:r:3"));
        var body = await JsonOf(res);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("dropped", body.GetProperty("status").GetString());

        var resolve = await fx.Client.GetAsync($"/v1/cluster/resolve?cluster_anchor={Cluster}");
        Assert.Equal(HttpStatusCode.NotFound, resolve.StatusCode);
    }

    [Fact]
    public async Task Announce_BeyondThreeHops_IsSilentlyDropped()
    {
        await using var fx = await Fixture.CreateAsync();

        var res = await fx.Client.SendAsync(Forwarded("/v1/announce", Member(AnchorB, epoch: 9),
            "urn:nps:agent:r:1", "urn:nps:agent:r:2", "urn:nps:agent:r:3"));
        var body = await JsonOf(res);

        Assert.Equal("dropped", body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await fx.Client.GetAsync($"/v1/cluster/resolve?cluster_anchor={Cluster}")).StatusCode);
    }

    [Fact]
    public async Task Announce_ForwardedOnce_IsIngestedAndHopAppended()
    {
        await using var fx = await Fixture.CreateAsync();

        var body = await JsonOf(await fx.Client.SendAsync(
            Forwarded("/v1/announce", Member(AnchorB, epoch: 2), PeerNid)));

        Assert.Equal("registered", body.GetProperty("status").GetString());
        Assert.Equal($"{PeerNid}, {OwnNid}", body.GetProperty("forwarded_by").GetString());
    }

    [Fact]
    public async Task Federation_OnNonFederatedProfile_Returns403()
    {
        await using var fx = await Fixture.CreateAsync(RegistryOptions.ProfileLocalDev);

        var res  = await fx.Client.SendAsync(Forwarded("/v1/federation/cluster", new
        {
            cluster_anchor = Cluster,
            cluster_epoch  = 4,
            active_nid     = AnchorB,
        }, PeerNid));
        var body = await JsonOf(res);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(NdpErrorCodes.AnnounceProfileViolation, body.GetProperty("error").GetString());
        Assert.Equal("NPS-AUTH-FORBIDDEN", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task FederationClusters_ListsPropagatableTuples()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorB, epoch: 2));

        var body     = await JsonOf(await fx.Client.GetAsync("/v1/federation/clusters"));
        var clusters = body.GetProperty("clusters").EnumerateArray().ToList();

        Assert.Equal(OwnNid, body.GetProperty("registry_nid").GetString());
        Assert.Single(clusters);
        Assert.Equal(Cluster, clusters[0].GetProperty("cluster_anchor").GetString());
        Assert.Equal(2UL, clusters[0].GetProperty("cluster_epoch").GetUInt64());
        Assert.Equal(AnchorB, clusters[0].GetProperty("active_nid").GetString());
    }

    // ── Untouched surfaces stay untouched (backward compatibility) ────────────

    [Fact]
    public async Task Health_ReportsProfileAndClusterCount()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorB, epoch: 2));

        var body = await JsonOf(await fx.Client.GetAsync("/health"));

        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("entries").GetInt32());
        Assert.Equal(1, body.GetProperty("clusters").GetInt32());
        Assert.Equal(RegistryOptions.ProfilePublicFederated, body.GetProperty("profile").GetString());
    }

    [Fact]
    public async Task Resolve_StillResolvesPlainNwpTargets()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Client.PostAsJsonAsync("/v1/announce", new AnnounceFrame
        {
            Nid          = "urn:nps:node:api.test:products",
            NodeType     = "memory",
            Addresses    = [new NdpAddress { Host = "10.0.0.5", Port = 17434, Protocol = "nwp" }],
            Capabilities = ["nwp:query"],
            Ttl          = 300,
            Timestamp    = DateTime.UtcNow.ToString("O"),
            Signature    = "ed25519:placeholder",
        });

        var body = await JsonOf(await fx.Client.GetAsync("/v1/resolve?target=nwp://api.test/products"));
        Assert.Equal("10.0.0.5", body.GetProperty("resolved").GetProperty("host").GetString());
    }

    [Fact]
    public async Task Graph_CarriesClusterAnchorOnNodes()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Client.PostAsJsonAsync("/v1/announce", Member(AnchorB, epoch: 2));

        var body = await JsonOf(await fx.Client.GetAsync("/v1/graph"));
        var node = body.GetProperty("nodes").EnumerateArray().Single();

        Assert.Equal(AnchorB, node.GetProperty("nid").GetString());
        Assert.Equal(Cluster, node.GetProperty("cluster_anchor").GetString());
    }
}
