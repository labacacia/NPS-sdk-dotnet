// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using NPS.NWP.Anchor;
using NPS.NWP.Anchor.Client;
using NPS.NWP.Anchor.Topology;
using NPS.NWP.Frames;
using NPS.NWP.Http;

namespace NPS.Tests.Nwp.Anchor;

/// <summary>
/// End-to-end conformance tests for L2-08
/// (NPS-CR-0002 — <c>topology.snapshot</c> + <c>topology.stream</c>).
///
/// <para>Each <c>[Fact]</c> implements one of the seven <c>TC-N2-*</c> cases
/// from <c>spec/services/conformance/NPS-Node-L2.md §3.1</c>. The IUT is the
/// <see cref="AnchorNodeMiddleware"/> with
/// <see cref="InMemoryAnchorTopologyService"/> as the topology provider; the
/// peer is <see cref="AnchorNodeClient"/> talking over a TestServer
/// <see cref="HttpClient"/>.</para>
/// </summary>
public sealed class AnchorTopologyTests : IAsyncLifetime
{
    private const string AnchorNid = "urn:nps:node:test:anchor-01";
    private const string AgentNid  = "urn:nps:agent:test:caller";

    private IHost                          _host        = null!;
    private HttpClient                     _http        = null!;
    private InMemoryAnchorTopologyService  _topology    = null!;
    private AnchorNodeClient               _client      = null!;

    public async Task InitializeAsync()
    {
        _topology = new InMemoryAnchorTopologyService(AnchorNid, retention: 5);
        _host     = await BuildHost(_topology);
        _http     = _host.GetTestClient();
        _http.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, AgentNid);
        _client   = new AnchorNodeClient(_http, pathPrefix: "/anchor");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ── TC-N2-AnchorTopo-01 ──────────────────────────────────────────────────

    [Fact]
    public async Task TopologySnapshot_BasicCluster_ListsAllMembers()
    {
        _topology.MemberJoinedAnnounce("urn:nps:node:test:m1", new[] { "memory" }, "ephemeral");
        _topology.MemberJoinedAnnounce("urn:nps:node:test:m2", new[] { "action" }, "ephemeral");
        _topology.MemberJoinedAnnounce("urn:nps:node:test:m3", new[] { "complex" }, "resident");

        var snap = await _client.GetSnapshotAsync();

        Assert.Equal(AnchorNid, snap.AnchorNid);
        Assert.Equal(3u, snap.ClusterSize);
        var nids = snap.Members.Select(m => m.Nid).ToHashSet();
        Assert.Contains("urn:nps:node:test:m1", nids);
        Assert.Contains("urn:nps:node:test:m2", nids);
        Assert.Contains("urn:nps:node:test:m3", nids);
        Assert.All(snap.Members, m =>
        {
            Assert.False(string.IsNullOrEmpty(m.Nid));
            Assert.NotEmpty(m.NodeRoles);
            Assert.False(string.IsNullOrEmpty(m.ActivationMode));
        });
        Assert.True(snap.Version > 0);
        Assert.True(snap.Truncated is null or false);
    }

    // ── TC-N2-AnchorTopo-02 ──────────────────────────────────────────────────

    [Fact]
    public async Task TopologySnapshot_VersionMonotonic_AcrossJoins()
    {
        _topology.MemberJoinedAnnounce("urn:nps:node:test:m1", new[] { "memory" }, "ephemeral");
        var before = await _client.GetSnapshotAsync();

        _topology.MemberJoinedAnnounce("urn:nps:node:test:m2", new[] { "action" }, "ephemeral");
        var after  = await _client.GetSnapshotAsync();

        Assert.True(after.Version > before.Version,
            $"version must strictly increase: before={before.Version}, after={after.Version}");
        Assert.Equal(before.ClusterSize + 1, after.ClusterSize);
    }

    // ── TC-N2-AnchorTopo-03 ──────────────────────────────────────────────────

    [Fact]
    public async Task TopologySnapshot_SubAnchorMember_HasChildAnchorAndCount()
    {
        _topology.MemberJoinedAnnounce(
            nid:            "urn:nps:node:test:child-anchor",
            nodeRoles:      new[] { "anchor" },
            activationMode: "resident",
            childAnchor:    true,
            memberCount:    2);

        var snap = await _client.GetSnapshotAsync();

        var child = Assert.Single(snap.Members);
        Assert.True(child.ChildAnchor);
        Assert.Equal(2u, child.MemberCount);
        Assert.True(snap.Truncated is null or false);
    }

    // ── TC-N2-AnchorStream-01 ────────────────────────────────────────────────

    [Fact]
    public async Task TopologyStream_MemberJoin_PushesEvent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start the subscription; once it returns the first event we're done.
        var subscriptionTask = ReadFirstEventAsync(filter: null, sinceVersion: null, cts.Token);

        // Wait until the server has actually registered the subscriber —
        // a fixed delay races on busy CI; polling SubscriberCount is the
        // robust fix.
        await WaitUntilSubscribedAsync(cts.Token);
        _topology.MemberJoinedAnnounce("urn:nps:node:test:m1", new[] { "memory" }, "ephemeral");

        var ev = await subscriptionTask;

        var joined = Assert.IsType<MemberJoined>(ev);
        Assert.Equal("urn:nps:node:test:m1", joined.Member.Nid);
        Assert.True(joined.Version > 0);
    }

    // ── TC-N2-AnchorStream-02 ────────────────────────────────────────────────

    [Fact]
    public async Task TopologyStream_MemberLeave_PushesEvent()
    {
        _topology.MemberJoinedAnnounce("urn:nps:node:test:m1", new[] { "memory" }, "ephemeral");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscriptionTask = ReadFirstEventAsync(filter: null, sinceVersion: null, cts.Token);

        await Task.Delay(50, cts.Token);
        _topology.MemberLeftAnnounce("urn:nps:node:test:m1");

        var ev = await subscriptionTask;
        var left = Assert.IsType<MemberLeft>(ev);
        Assert.Equal("urn:nps:node:test:m1", left.Nid);
    }

    // ── TC-N2-AnchorStream-03 ────────────────────────────────────────────────

    [Fact]
    public async Task TopologyStream_ResumeFromVersion_ReplaysOnly()
    {
        var v1 = _topology.MemberJoinedAnnounce("urn:nps:node:test:m1", new[] { "memory" }, "ephemeral");
        var v2 = _topology.MemberJoinedAnnounce("urn:nps:node:test:m2", new[] { "action" },  "ephemeral");
        var v3 = _topology.MemberJoinedAnnounce("urn:nps:node:test:m3", new[] { "complex" }, "resident");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = await CollectAsync(filter: null, sinceVersion: v1, target: 2, cts.Token);

        Assert.Equal(2, events.Count);
        var first  = Assert.IsType<MemberJoined>(events[0]);
        var second = Assert.IsType<MemberJoined>(events[1]);
        Assert.Equal("urn:nps:node:test:m2", first.Member.Nid);
        Assert.Equal("urn:nps:node:test:m3", second.Member.Nid);
        Assert.Equal(v2, first.Version);
        Assert.Equal(v3, second.Version);
    }

    // ── TC-N2-AnchorStream-04 ────────────────────────────────────────────────

    [Fact]
    public async Task TopologyStream_VersionTooOld_EmitsResyncRequired()
    {
        // Retention is 5 (set in InitializeAsync). Push 10 events so v=1 is well outside the window.
        for (int i = 1; i <= 10; i++)
        {
            _topology.MemberJoinedAnnounce(
                $"urn:nps:node:test:m{i}", new[] { "memory" }, "ephemeral");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ev = await ReadFirstEventAsync(filter: null, sinceVersion: 1, cts.Token);

        var resync = Assert.IsType<ResyncRequired>(ev);
        Assert.Equal("version_too_old", resync.Reason);
    }

    // ── TC-N2-AnchorTopo-04 ──────────────────────────────────────────────────

    [Fact]
    public async Task TopologySnapshot_MissingCapability_Returns403()
    {
        // Build a separate host that requires topology:read capability.
        using var capHost = await BuildHost(_topology, opts => opts.RequireTopologyCapability = true);
        using var capHttp = capHost.GetTestClient();
        capHttp.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, AgentNid);
        // Deliberately do NOT add X-NWP-Capabilities: topology:read

        var resp = await capHttp.PostAsync("/anchor/query",
            new StringContent(
                JsonSerializer.Serialize(new { type = "topology.snapshot" }),
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains(NwpTopologyErrorCodes.Unauthorized, await resp.Content.ReadAsStringAsync());
    }

    // ── Negative-path coverage (filter / scope errors) ──────────────────────

    [Fact]
    public async Task TopologySnapshot_UnknownScope_Returns400()
    {
        // Bypass the typed client to send a raw payload with an invalid scope.
        var payload = new
        {
            type     = "topology.snapshot",
            topology = new { scope = "not-a-real-scope" },
        };
        var resp = await _http.PostAsync("/anchor/query",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains(NwpTopologyErrorCodes.UnsupportedScope, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TopologyStream_UnsupportedFilterKey_Returns400()
    {
        var payload = new
        {
            type      = "topology.stream",
            action    = "subscribe",
            stream_id = Guid.NewGuid().ToString("N"),
            topology  = new { scope = "cluster", filter = new { something_weird = new[] { "x" } } },
        };
        var resp = await _http.PostAsync("/anchor/subscribe",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains(NwpTopologyErrorCodes.FilterUnsupported, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TopologyQuery_UnknownReservedType_Returns501WithCorrectCode()
    {
        // TC-N2-AnchorTopo-08: unknown reserved type MUST yield NWP-RESERVED-TYPE-UNSUPPORTED
        // and MUST NOT yield NWP-ACTION-NOT-FOUND.
        var payload = new { type = "topology.imaginary" };
        var resp = await _http.PostAsync("/anchor/query",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(NwpTopologyErrorCodes.ReservedTypeUnsupported, body);
        Assert.DoesNotContain(NwpErrorCodes.ActionNotFound, body);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task WaitUntilSubscribedAsync(CancellationToken ct)
    {
        // The server adds a subscriber to its registry under the gate before
        // emitting the ack line back to the client; once SubscriberCount
        // moves above zero the subsequent state mutation is guaranteed to
        // fan-out to it.
        while (_topology.SubscriberCount == 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5, ct);
        }
    }

    /// <summary>
    /// Subscribe, return the first <see cref="TopologyEvent"/> the server
    /// pushes, then dispose the subscription. Used by the join / leave / resync
    /// scenarios where we only care about the first push.
    /// </summary>
    private async Task<TopologyEvent> ReadFirstEventAsync(
        TopologyFilter?   filter,
        ulong?            sinceVersion,
        CancellationToken ct)
    {
        await foreach (var ev in _client.SubscribeAsync(filter, sinceVersion, ct: ct))
            return ev;
        throw new InvalidOperationException("subscription closed without delivering an event.");
    }

    /// <summary>
    /// Subscribe and collect up to <paramref name="target"/> events (or until
    /// the stream ends / cancellation fires). Used when the test needs to
    /// verify the order of multiple replayed events.
    /// </summary>
    private async Task<List<TopologyEvent>> CollectAsync(
        TopologyFilter?   filter,
        ulong?            sinceVersion,
        int               target,
        CancellationToken ct)
    {
        var collected = new List<TopologyEvent>(target);
        await foreach (var ev in _client.SubscribeAsync(filter, sinceVersion, ct: ct))
        {
            collected.Add(ev);
            if (collected.Count >= target) break;
        }
        return collected;
    }

    private static async Task<IHost> BuildHost(
        InMemoryAnchorTopologyService topology,
        Action<AnchorNodeOptions>? configure = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton<IAnchorRouter>(new ThrowingRouter());
                    services.AddSingleton<INopOrchestrator>(new ThrowingOrchestrator());
                    services.AddSingleton<IAnchorRateLimiter, InMemoryAnchorRateLimiter>();
                    services.AddSingleton<IAnchorTopologyService>(topology);
                });
                web.Configure(app =>
                {
                    app.UseAnchorNode(opts =>
                    {
                        opts.NodeId      = AnchorNid;
                        opts.DisplayName = "Test Anchor";
                        opts.PathPrefix  = "/anchor";
                        opts.Actions     = new Dictionary<string, AnchorActionSpec>();
                        opts.RequireAuth = true;
                        opts.RateLimits  = new AnchorRateLimits
                        {
                            RequestsPerMinute = 600,
                            MaxConcurrent     = 100,
                            CgnPerHour        = 1_000_000,
                        };
                        configure?.Invoke(opts);
                    });
                    app.Run(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
                });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private sealed class ThrowingRouter : IAnchorRouter
    {
        public Task<TaskFrame> BuildTaskAsync(
            ActionFrame frame, AnchorRouteContext ctx, CancellationToken cancel = default)
            => throw new InvalidOperationException(
                "topology tests must not exercise /invoke.");
    }

    private sealed class ThrowingOrchestrator : INopOrchestrator
    {
        public Task<NopTaskResult> ExecuteAsync(TaskFrame task, CancellationToken ct = default)
            => throw new InvalidOperationException(
                "topology tests must not exercise /invoke.");
        public Task CancelAsync(string taskId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<NopTaskRecord?> GetStatusAsync(string taskId, CancellationToken ct = default)
            => Task.FromResult<NopTaskRecord?>(null);
    }
}
