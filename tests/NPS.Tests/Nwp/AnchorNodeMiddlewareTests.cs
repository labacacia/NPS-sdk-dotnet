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
using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using NPS.NWP.Frames;
using NPS.NWP.Anchor;
using NPS.NWP.Http;

namespace NPS.Tests.Nwp;

/// <summary>
/// Integration tests for <see cref="AnchorNodeMiddleware"/>. Uses a fake
/// <see cref="IAnchorRouter"/> that records the <see cref="TaskFrame"/>
/// produced for each action, and a fake <see cref="INopOrchestrator"/> so
/// tests stay hermetic.
/// </summary>
public sealed class AnchorNodeMiddlewareTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly IReadOnlyDictionary<string, AnchorActionSpec> Actions =
        new Dictionary<string, AnchorActionSpec>
        {
            ["analysis.run"] = new()
            {
                Description       = "Run a multi-step data analysis pipeline",
                Async             = true,
                EstimatedNpt      = 2000,
                TimeoutMsDefault  = 60_000,
                TimeoutMsMax      = 120_000,
                RequiredCapability = "agent:invoke",
            },
            ["analysis.echo"] = new()
            {
                Description = "Echo parameters back (sync).",
                Async       = false,
            },
        };

    private IHost                _host         = null!;
    private HttpClient           _client       = null!;
    private FakeAnchorRouter    _router       = null!;
    private FakeNopOrchestrator  _orchestrator = null!;

    public async Task InitializeAsync()
    {
        _router       = new FakeAnchorRouter();
        _orchestrator = new FakeNopOrchestrator();
        _host         = await BuildHost();
        _client       = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ── /.nwm advertisement ─────────────────────────────────────────────────

    [Fact]
    public async Task Nwm_AdvertisesAnchorTypeAndActions()
    {
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");

        var resp = await _client.GetAsync("/gw/.nwm");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("anchor", resp.Headers.GetValues(NwpHttpHeaders.NodeType).Single());
        Assert.Equal(NwpHttpHeaders.MimeManifest, resp.Content.Headers.ContentType!.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal("anchor", doc.RootElement.GetProperty("node_type").GetString());
        Assert.Equal("urn:nps:node:test:gw", doc.RootElement.GetProperty("node_id").GetString());

        // Actions embedded directly in the NWM (NPS-AaaS §2.3).
        var actions = doc.RootElement.GetProperty("actions");
        Assert.Equal(2, actions.GetArrayLength());
        var ids = actions.EnumerateArray().Select(a => a.GetProperty("action_id").GetString()).ToHashSet();
        Assert.Contains("analysis.run",  ids);
        Assert.Contains("analysis.echo", ids);

        // Rate limits block.
        Assert.True(doc.RootElement.TryGetProperty("rate_limits", out var rl));
        Assert.Equal(60u, rl.GetProperty("requests_per_minute").GetUInt32());

        // Auth declaration.
        var auth = doc.RootElement.GetProperty("auth");
        Assert.True(auth.GetProperty("required").GetBoolean());
        Assert.Equal("nip-cert", auth.GetProperty("identity_type").GetString());
    }

    [Fact]
    public async Task Nwm_AuthRequired_WithoutHeaderReturns401()
    {
        var resp = await _client.GetAsync("/gw/.nwm");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── /invoke sync happy path ─────────────────────────────────────────────

    [Fact]
    public async Task Invoke_SyncAction_DispatchesToOrchestratorAndReturnsCaps()
    {
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");

        // Prime fake orchestrator to return a successful aggregated payload.
        var payload = JsonDocument.Parse("""{"ok":true,"rows":7}""").RootElement;
        _orchestrator.NextResult = NopTaskResult.Success(
            taskId:         "ignored",     // overwritten by router's task id
            aggregatedResult: payload,
            nodeResults:    new Dictionary<string, JsonElement>());

        var frame = new ActionFrame
        {
            ActionId  = "analysis.echo",
            RequestId = Guid.NewGuid().ToString(),
        };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/gw/invoke", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("anchor", resp.Headers.GetValues(NwpHttpHeaders.NodeType).Single());

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":true",  body);
        Assert.Contains("\"rows\":7",   body);

        // Router saw the action frame.
        Assert.Single(_router.Built);
        Assert.Equal("analysis.echo", _router.Built[0].ActionId);

        // Orchestrator saw the built task frame.
        Assert.Single(_orchestrator.Dispatched);
        Assert.Equal(_router.LastTaskId, _orchestrator.Dispatched[0].TaskId);
    }

    // ── /invoke async path ──────────────────────────────────────────────────

    [Fact]
    public async Task Invoke_AsyncAction_ReturnsTaskHandle202()
    {
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");
        _orchestrator.HoldCompletion = true;    // keep ExecuteAsync hanging until DisposeAsync

        var frame = new ActionFrame { ActionId = "analysis.run", Async = true };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/gw/invoke", content);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(_router.LastTaskId, doc.RootElement.GetProperty("task_id").GetString());

        _orchestrator.Release();                // let the background task finish
    }

    [Fact]
    public async Task Invoke_AsyncOnSyncAction_Returns400()
    {
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");
        var frame = new ActionFrame { ActionId = "analysis.echo", Async = true };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/gw/invoke", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains(NwpErrorCodes.ActionParamsInvalid, await resp.Content.ReadAsStringAsync());
    }

    // ── Unknown / invalid input handling ────────────────────────────────────

    [Fact]
    public async Task Invoke_UnknownAction_Returns404()
    {
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");
        var frame = new ActionFrame { ActionId = "does.not.exist" };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/gw/invoke", content);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains(NwpErrorCodes.ActionNotFound, await resp.Content.ReadAsStringAsync());

        // Router must never see unknown actions.
        Assert.Empty(_router.Built);
    }

    [Fact]
    public async Task Invoke_InvalidPriority_Returns400()
    {
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");
        var frame = new ActionFrame { ActionId = "analysis.echo", Priority = "URGENT" };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/gw/invoke", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Trace context propagation ───────────────────────────────────────────

    [Fact]
    public async Task Invoke_GeneratesTraceContext_WhenAbsent()
    {
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");
        _orchestrator.NextResult = NopTaskResult.Success(
            taskId: "unused", aggregatedResult: null,
            nodeResults: new Dictionary<string, JsonElement>());

        var frame = new ActionFrame { ActionId = "analysis.echo" };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/gw/invoke", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var ctxSeen = _router.LastContext!;
        Assert.NotNull(ctxSeen);
        Assert.NotNull(ctxSeen.TraceId);
        Assert.Equal(32, ctxSeen.TraceId!.Length);  // 16 bytes hex
        Assert.NotNull(ctxSeen.SpanId);
        Assert.Equal(16, ctxSeen.SpanId!.Length);
    }

    [Fact]
    public async Task Invoke_RespectsInboundTraceparent()
    {
        _orchestrator.NextResult = NopTaskResult.Success(
            taskId: "unused", aggregatedResult: null,
            nodeResults: new Dictionary<string, JsonElement>());

        const string traceId      = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string inboundSpan  = "00f067aa0ba902b7";
        var req = new HttpRequestMessage(HttpMethod.Post, "/gw/invoke")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new ActionFrame { ActionId = "analysis.echo" }, JsonOpts),
                Encoding.UTF8, NwpHttpHeaders.MimeFrame),
        };
        req.Headers.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");
        req.Headers.Add("traceparent", $"00-{traceId}-{inboundSpan}-01");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var seen = _router.LastContext!;
        Assert.Equal(traceId, seen.TraceId);           // propagated
        Assert.NotEqual(inboundSpan, seen.SpanId);     // anchor forked a new span
    }

    // ── Rate limiting ───────────────────────────────────────────────────────

    [Fact]
    public async Task Invoke_ExceedsRequestsPerMinute_Returns429()
    {
        _host.Dispose();
        _host = await BuildHost(rateLimits: new AnchorRateLimits
        {
            RequestsPerMinute = 1,
        });
        _client = _host.GetTestClient();
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");

        _orchestrator.NextResult = NopTaskResult.Success(
            taskId: "unused", aggregatedResult: null,
            nodeResults: new Dictionary<string, JsonElement>());

        var body1 = new StringContent(
            JsonSerializer.Serialize(new ActionFrame { ActionId = "analysis.echo" }, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var first = await _client.PostAsync("/gw/invoke", body1);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var body2 = new StringContent(
            JsonSerializer.Serialize(new ActionFrame { ActionId = "analysis.echo" }, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var second = await _client.PostAsync("/gw/invoke", body2);
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        Assert.Contains(NwpErrorCodes.BudgetExceeded,
            await second.Content.ReadAsStringAsync());
        Assert.True(second.Headers.Contains("Retry-After"));
    }

    // ── Orchestrator-reported failure surfaces as ErrorFrame ────────────────

    [Fact]
    public async Task Invoke_OrchestratorFailure_ReturnsErrorFrame()
    {
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");
        _orchestrator.NextResult = NopTaskResult.Failure(
            taskId:     "ignored",
            errorCode:  "NOP-TASK-FAILED",
            errorMessage: "worker exploded");

        var frame = new ActionFrame { ActionId = "analysis.echo" };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts),
            Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/gw/invoke", content);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("NOP-TASK-FAILED",  body);
        Assert.Contains("worker exploded",  body);
    }

    // ── Wrong HTTP method / path pass-through ───────────────────────────────

    [Fact]
    public async Task Invoke_Get_Returns405()
    {
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");
        var resp = await _client.GetAsync("/gw/invoke");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Fact]
    public async Task UnknownPath_PassesThrough()
    {
        _client.DefaultRequestHeaders.Add(NwpHttpHeaders.Agent, "urn:nps:agent:caller");
        var resp = await _client.GetAsync("/other");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Host builder ────────────────────────────────────────────────────────

    private async Task<IHost> BuildHost(AnchorRateLimits? rateLimits = null)
    {
        var router       = _router;
        var orchestrator = _orchestrator;
        var limits       = rateLimits ?? new AnchorRateLimits
        {
            RequestsPerMinute = 60,
            MaxConcurrent     = 10,
            NptPerHour        = 100_000,
        };

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton<IAnchorRouter>(router);
                    services.AddSingleton<INopOrchestrator>(orchestrator);
                    services.AddSingleton<IAnchorRateLimiter, InMemoryAnchorRateLimiter>();
                });
                web.Configure(app =>
                {
                    app.UseAnchorNode(opts =>
                    {
                        opts.NodeId      = "urn:nps:node:test:gw";
                        opts.DisplayName = "Test Anchor";
                        opts.PathPrefix  = "/gw";
                        opts.Actions     = Actions;
                        opts.RequireAuth = true;
                        opts.RateLimits  = limits;
                        opts.RequiredCapabilities = new[] { "agent:invoke" };
                    });
                    app.Run(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
                });
            })
            .Build();
        await host.StartAsync();
        return host;
    }
}

// ── Fakes ───────────────────────────────────────────────────────────────────

internal sealed class FakeAnchorRouter : IAnchorRouter
{
    public List<ActionFrame> Built       { get; } = new();
    public TaskContext?      LastContext { get; private set; }
    public string?           LastTaskId  { get; private set; }

    public Task<TaskFrame> BuildTaskAsync(
        ActionFrame frame, AnchorRouteContext ctx, CancellationToken cancel = default)
    {
        Built.Add(frame);
        LastContext = ctx.TraceContext;
        LastTaskId  = Guid.NewGuid().ToString("N");

        var task = new TaskFrame
        {
            TaskId = LastTaskId,
            Dag = new TaskDag
            {
                Nodes = new[]
                {
                    new DagNode
                    {
                        Id     = "only",
                        Action = "nwp://worker/invoke",
                        Agent  = "urn:nps:agent:worker",
                    },
                },
                Edges = Array.Empty<DagEdge>(),
            },
            Priority  = ctx.Priority,
            TimeoutMs = ctx.EffectiveTimeoutMs,
            RequestId = ctx.RequestId,
            Context   = ctx.TraceContext,
        };
        return Task.FromResult(task);
    }
}

internal sealed class FakeNopOrchestrator : INopOrchestrator
{
    public List<TaskFrame>          Dispatched   { get; } = new();
    public NopTaskResult?           NextResult   { get; set; }
    public bool                     HoldCompletion { get; set; }

    private readonly TaskCompletionSource<bool> _gate = new();

    public void Release() => _gate.TrySetResult(true);

    public async Task<NopTaskResult> ExecuteAsync(TaskFrame task, CancellationToken ct = default)
    {
        Dispatched.Add(task);
        if (HoldCompletion) await _gate.Task.WaitAsync(ct);
        return NextResult ?? NopTaskResult.Success(
            task.TaskId, null, new Dictionary<string, JsonElement>());
    }

    public Task CancelAsync(string taskId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<NopTaskRecord?> GetStatusAsync(string taskId, CancellationToken ct = default)
        => Task.FromResult<NopTaskRecord?>(null);
}
