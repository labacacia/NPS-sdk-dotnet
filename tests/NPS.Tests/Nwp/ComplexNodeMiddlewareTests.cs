// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NPS.NWP.ActionNode;
using NPS.NWP.ComplexNode;
using NPS.NWP.Extensions;
using NPS.NWP.Frames;
using NPS.NWP.Http;
using NPS.NWP.MemoryNode;

namespace NPS.Tests.Nwp;

/// <summary>
/// Integration tests for <see cref="ComplexNodeMiddleware"/> using ASP.NET Core TestHost.
/// Outbound child-node fetches are captured by a <see cref="StubChildHandler"/> so no
/// real network I/O is involved.
/// </summary>
public sealed class ComplexNodeMiddlewareTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly MemoryNodeSchema LocalSchema = new()
    {
        TableName  = "orders",
        PrimaryKey = "id",
        Fields =
        [
            new MemoryNodeField { Name = "id",     Type = "number" },
            new MemoryNodeField { Name = "status", Type = "string" },
        ],
    };

    private static readonly IReadOnlyDictionary<string, ActionSpec> Actions =
        new Dictionary<string, ActionSpec>
        {
            ["orders.cancel"] = new() { Description = "Cancel an order", Async = false },
        };

    private IHost          _host      = null!;
    private HttpClient     _client    = null!;
    private FakeComplexProvider _provider = null!;
    private StubChildHandler _childHandler = null!;

    public async Task InitializeAsync()
    {
        _provider     = new FakeComplexProvider();
        _childHandler = new StubChildHandler();
        _host         = await BuildHost();
        _client       = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ── /.nwm ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nwm_ReturnsComplexManifestWithGraph()
    {
        var resp = await _client.GetAsync("/orders/.nwm");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(NwpHttpHeaders.MimeManifest, resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("complex", resp.Headers.GetValues(NwpHttpHeaders.NodeType).First());

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("complex", root.GetProperty("node_type").GetString());
        Assert.True(root.TryGetProperty("graph", out var graph));
        var refs = graph.GetProperty("refs").EnumerateArray().ToList();
        Assert.Equal(2, refs.Count);
        Assert.Equal("user",    refs[0].GetProperty("rel").GetString());
        Assert.Equal("payment", refs[1].GetProperty("rel").GetString());
        Assert.Equal((uint)2, graph.GetProperty("max_depth").GetUInt32());
    }

    // ── /query — local only (depth=0) ────────────────────────────────────────

    [Fact]
    public async Task Query_DepthZero_ReturnsOnlyLocalData()
    {
        _provider.QueryRows = new[]
        {
            (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = 42, ["status"] = "paid",
            },
        };

        var frame   = new QueryFrame { Limit = 10 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/orders/query", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1u, doc.RootElement.GetProperty("count").GetUInt32());
        Assert.False(doc.RootElement.TryGetProperty("graph", out _));
        Assert.Equal(0, _childHandler.CallCount);  // no child fetches
    }

    // ── /query — depth=1 triggers graph expansion ───────────────────────────

    [Fact]
    public async Task Query_DepthOne_FanOutsToChildren()
    {
        _provider.QueryRows = Array.Empty<IReadOnlyDictionary<string, object?>>();

        _childHandler.SetResponse("https://child.example.com/users/query",
            """{"anchor_ref":"sha256:u","count":1,"data":[{"id":"u1"}]}""");
        _childHandler.SetResponse("https://child.example.com/payments/query",
            """{"anchor_ref":"sha256:p","count":1,"data":[{"id":"p1"}]}""");

        var frame   = new QueryFrame { Limit = 10 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders/query") { Content = content };
        req.Headers.Add(NwpHttpHeaders.Depth, "1");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("graph", out var graph));
        var arr = graph.EnumerateArray().ToList();
        Assert.Equal(2, arr.Count);

        var user = arr.Single(e => e.GetProperty("rel").GetString() == "user");
        Assert.Equal(1u, user.GetProperty("data").GetProperty("count").GetUInt32());

        var payment = arr.Single(e => e.GetProperty("rel").GetString() == "payment");
        Assert.Equal(1u, payment.GetProperty("data").GetProperty("count").GetUInt32());

        // Two outbound calls, both with Depth=0 and carrying the parent's node_id in trace.
        Assert.Equal(2, _childHandler.CallCount);
        Assert.All(_childHandler.Sent, req =>
        {
            Assert.Equal("0", req.Headers.GetValues(NwpHttpHeaders.Depth).First());
            Assert.Contains("urn:nps:node:test:orders",
                req.Headers.GetValues(ComplexNodeMiddleware.TraceHeader).First());
        });
    }

    // ── /query — depth exceeding max_depth is rejected ───────────────────────

    [Fact]
    public async Task Query_DepthOverMax_Returns400WithDepthExceeded()
    {
        var frame   = new QueryFrame { Limit = 10 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders/query") { Content = content };
        req.Headers.Add(NwpHttpHeaders.Depth, "5");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(NwpErrorCodes.DepthExceeded, body);
    }

    // ── /query — cycle detection via X-NWP-Trace ─────────────────────────────

    [Fact]
    public async Task Query_TraceContainsSelf_Returns422GraphCycle()
    {
        var frame   = new QueryFrame { Limit = 10 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders/query") { Content = content };
        req.Headers.Add(ComplexNodeMiddleware.TraceHeader,
            "urn:nps:node:test:upstream, urn:nps:node:test:orders");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(NwpErrorCodes.GraphCycle, body);
    }

    // ── /query — child URL rejected by allowlist ────────────────────────────

    [Fact]
    public async Task Query_ChildOutsideAllowlist_EmitsEmbeddedError()
    {
        // Spin up a host whose options include an off-allowlist child.
        _host.Dispose();
        _provider     = new FakeComplexProvider { QueryRows = Array.Empty<IReadOnlyDictionary<string, object?>>() };
        _childHandler = new StubChildHandler();
        _host = await BuildHost(extraGraphRef: new ComplexGraphRef(
            "evil", "https://attacker.invalid/hook"));
        _client = _host.GetTestClient();

        var frame   = new QueryFrame { Limit = 10 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders/query") { Content = content };
        req.Headers.Add(NwpHttpHeaders.Depth, "1");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.GetProperty("graph").EnumerateArray().ToList();
        var evil = arr.Single(e => e.GetProperty("rel").GetString() == "evil");
        Assert.True(evil.TryGetProperty("error", out var err));
        Assert.Equal(NwpErrorCodes.AuthNidScopeViolation,
            err.GetProperty("code").GetString());

        // 2 fetches for the whitelisted refs, none for the evil one.
        Assert.Equal(2, _childHandler.CallCount);
    }

    // ── /query — child failure reported as embedded error ───────────────────

    [Fact]
    public async Task Query_ChildReturns500_EmitsEmbeddedError()
    {
        _provider.QueryRows = Array.Empty<IReadOnlyDictionary<string, object?>>();
        _childHandler.SetResponse("https://child.example.com/users/query",
            "boom", HttpStatusCode.InternalServerError);
        _childHandler.SetResponse("https://child.example.com/payments/query",
            """{"anchor_ref":"sha256:p","count":0,"data":[]}""");

        var frame   = new QueryFrame { Limit = 10 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders/query") { Content = content };
        req.Headers.Add(NwpHttpHeaders.Depth, "1");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.GetProperty("graph").EnumerateArray().ToList();
        var user = arr.Single(e => e.GetProperty("rel").GetString() == "user");
        Assert.True(user.TryGetProperty("error", out var err));
        Assert.Equal(NwpErrorCodes.NodeUnavailable, err.GetProperty("code").GetString());
    }

    // ── /invoke — declared action dispatched to local provider ──────────────

    [Fact]
    public async Task Invoke_LocalAction_ReturnsProviderResult()
    {
        _provider.ActionResult = JsonSerializer.SerializeToElement(
            new { cancelled = true }, JsonOpts);

        var frame = new ActionFrame { ActionId = "orders.cancel" };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/orders/invoke", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("cancelled", body);
    }

    // ── /invoke — unknown action returns NWP-ACTION-NOT-FOUND ────────────────

    [Fact]
    public async Task Invoke_UnknownAction_Returns404()
    {
        var frame = new ActionFrame { ActionId = "orders.unknown" };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/orders/invoke", content);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(NwpErrorCodes.ActionNotFound, body);
    }

    // ── /invoke — async flag rejected ────────────────────────────────────────

    [Fact]
    public async Task Invoke_AsyncRequested_Returns400()
    {
        var frame = new ActionFrame { ActionId = "orders.cancel", Async = true };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/orders/invoke", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(NwpErrorCodes.ActionParamsInvalid, body);
    }

    // ── Auth ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nwm_AuthRequired_WithoutHeaderReturns401()
    {
        _host.Dispose();
        _host = await BuildHost(requireAuth: true);
        _client = _host.GetTestClient();

        var resp = await _client.GetAsync("/orders/.nwm");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Path outside prefix passes through ───────────────────────────────────

    [Fact]
    public async Task UnknownPath_PassesThrough()
    {
        var resp = await _client.GetAsync("/other");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Construction guard: reserved action ids rejected ────────────────────

    [Fact]
    public async Task Ctor_ReservedActionIdRegistered_Throws()
    {
        // The middleware is built lazily on the first request, so we need to send one.
        using var h = await BuildHost(extraActions: new Dictionary<string, ActionSpec>
        {
            [ActionNodeMiddleware.SystemTaskStatus] = new() { Async = false },
        });
        using var c = h.GetTestClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.GetAsync("/orders/.nwm"));
    }

    // ── Host builder ─────────────────────────────────────────────────────────

    private async Task<IHost> BuildHost(
        bool                                 requireAuth      = false,
        ComplexGraphRef?                     extraGraphRef    = null,
        IDictionary<string, ActionSpec>?     extraActions     = null)
    {
        var provider = _provider;
        var handler  = _childHandler;

        var graph = new List<ComplexGraphRef>
        {
            new("user",    "https://child.example.com/users"),
            new("payment", "https://child.example.com/payments"),
        };
        if (extraGraphRef is not null) graph.Add(extraGraphRef);

        var actions = new Dictionary<string, ActionSpec>(Actions);
        if (extraActions is not null)
            foreach (var kv in extraActions) actions[kv.Key] = kv.Value;

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton<FakeComplexProvider>(_ => provider);
                    services.AddHttpClient(NwpServiceExtensions.ComplexNodeHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => handler);
                });
                web.Configure(app =>
                {
                    app.UseComplexNode<FakeComplexProvider>(opts =>
                    {
                        opts.NodeId     = "urn:nps:node:test:orders";
                        opts.DisplayName = "Test Orders";
                        opts.PathPrefix = "/orders";
                        opts.Schema     = LocalSchema;
                        opts.Actions    = actions;
                        opts.Graph      = graph;
                        opts.GraphMaxDepth = 2;
                        opts.AllowedChildUrlPrefixes = new[] { "https://child.example.com/" };
                        opts.RejectPrivateChildUrls  = true;
                        opts.RequireAuth = requireAuth;
                    });
                    app.Run(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
                });
            })
            .Build();
        await host.StartAsync();
        return host;
    }
}

// ── Fake local provider ──────────────────────────────────────────────────────

internal sealed class FakeComplexProvider : IComplexNodeProvider
{
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> QueryRows { get; set; }
        = Array.Empty<IReadOnlyDictionary<string, object?>>();

    public JsonElement? ActionResult { get; set; }

    public Task<MemoryNodeQueryResult> QueryAsync(
        QueryFrame frame, ComplexNodeOptions options, CancellationToken ct = default)
        => Task.FromResult(new MemoryNodeQueryResult { Rows = QueryRows });

    public Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame, ActionContext context, CancellationToken ct = default)
        => Task.FromResult(new ActionExecutionResult { Result = ActionResult });
}

// ── Stub outbound handler ────────────────────────────────────────────────────

internal sealed class StubChildHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses
        = new(StringComparer.OrdinalIgnoreCase);

    public List<HttpRequestMessage> Sent     { get; } = new();
    public int                      CallCount => Sent.Count;

    public void SetResponse(string url, string body, HttpStatusCode status = HttpStatusCode.OK)
        => _responses[url] = (status, body);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Sent.Add(request);
        var url = request.RequestUri!.ToString();
        var (status, body) = _responses.TryGetValue(url, out var r)
            ? r
            : (HttpStatusCode.OK, """{"anchor_ref":null,"count":0,"data":[]}""");
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, NwpHttpHeaders.MimeCapsule),
        });
    }
}
