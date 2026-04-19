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
using NPS.NWP.Extensions;
using NPS.NWP.Frames;
using NPS.NWP.Http;

namespace NPS.Tests.Nwp;

/// <summary>
/// Integration tests for <see cref="ActionNodeMiddleware"/> using ASP.NET Core TestHost.
/// Covers routing (/.nwm, /.schema, /actions, /invoke), sync + async execution,
/// idempotency, reserved system.task.* actions, SSRF guard, priority validation,
/// timeout clamping, and auth enforcement.
/// </summary>
public sealed class ActionNodeMiddlewareTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly IReadOnlyDictionary<string, ActionSpec> Actions =
        new Dictionary<string, ActionSpec>
        {
            ["orders.create"] = new()
            {
                Description      = "Create a new order",
                Async            = true,
                Idempotent       = true,
                TimeoutMsDefault = 5_000,
                TimeoutMsMax     = 60_000,
                ResultAnchor     = "sha256:order",
            },
            ["orders.ping"] = new()
            {
                Description = "Sync-only echo",
                Async       = false,
            },
        };

    private IHost         _host     = null!;
    private HttpClient    _client   = null!;
    private FakeActionProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _provider = new FakeActionProvider();
        _host     = await BuildHost(requireAuth: false);
        _client   = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ── /.nwm ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nwm_Get_Returns200ActionManifest()
    {
        var resp = await _client.GetAsync("/orders/.nwm");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(NwpHttpHeaders.MimeManifest, resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("action", resp.Headers.GetValues(NwpHttpHeaders.NodeType).First());

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"action\"", body);
        Assert.Contains("/orders/invoke", body);
    }

    [Fact]
    public async Task Nwm_TrailingSlash_Returns200()
    {
        var resp = await _client.GetAsync("/orders/.nwm/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── /actions ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Actions_Get_ReturnsRegistry()
    {
        var resp = await _client.GetAsync("/orders/actions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("orders.create", body);
        Assert.Contains("orders.ping",   body);
    }

    [Fact]
    public async Task Schema_Get_ReturnsRegistryJson()
    {
        var resp = await _client.GetAsync("/orders/.schema");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("actions", body);
    }

    // ── /invoke — sync happy path ────────────────────────────────────────────

    [Fact]
    public async Task Invoke_Sync_Returns200WithCaps()
    {
        var frame = new ActionFrame { ActionId = "orders.ping" };
        _provider.SyncResult = JsonSerializer.SerializeToElement(new { ok = true }, JsonOpts);

        var resp = await PostInvoke(frame);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("action", resp.Headers.GetValues(NwpHttpHeaders.NodeType).First());

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Invoke_UnknownActionId_Returns404()
    {
        var frame = new ActionFrame { ActionId = "orders.mystery" };

        var resp = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(NwpErrorCodes.ActionNotFound, body);
    }

    [Fact]
    public async Task Invoke_SyncOnlyActionWithAsyncTrue_Returns400()
    {
        var frame = new ActionFrame { ActionId = "orders.ping", Async = true };

        var resp = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(NwpErrorCodes.ActionParamsInvalid, body);
    }

    [Fact]
    public async Task Invoke_InvalidPriority_Returns400()
    {
        var frame = new ActionFrame { ActionId = "orders.ping", Priority = "urgent" };

        var resp = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Invoke_MalformedBody_Returns400()
    {
        var content = new StringContent("{{{", Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var resp = await _client.PostAsync("/orders/invoke", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Invoke_Get_Returns405()
    {
        var resp = await _client.GetAsync("/orders/invoke");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Fact]
    public async Task Invoke_RequestIdEchoedInResponseHeader()
    {
        _provider.SyncResult = JsonSerializer.SerializeToElement(new { ok = true }, JsonOpts);
        var rid = Guid.NewGuid().ToString();
        var frame = new ActionFrame { ActionId = "orders.ping", RequestId = rid };

        var resp = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(rid, resp.Headers.GetValues("X-NWP-Request-Id").First());
    }

    // ── /invoke — async path ─────────────────────────────────────────────────

    [Fact]
    public async Task Invoke_Async_Returns202WithTaskId()
    {
        _provider.AsyncCompletionDelay = TimeSpan.FromMilliseconds(200);
        _provider.SyncResult = JsonSerializer.SerializeToElement(new { created = true }, JsonOpts);

        var frame = new ActionFrame { ActionId = "orders.create", Async = true };

        var resp = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("task_id").GetString()));
        Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Invoke_Async_ThenStatusPolling_ReturnsTerminalState()
    {
        _provider.SyncResult = JsonSerializer.SerializeToElement(new { done = 1 }, JsonOpts);

        var frame = new ActionFrame { ActionId = "orders.create", Async = true };

        var resp = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var taskId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("task_id").GetString()!;

        // Wait for the background task to complete
        string? status = null;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(50);
            var poll = await PostInvoke(new ActionFrame
            {
                ActionId = ActionNodeMiddleware.SystemTaskStatus,
                Params   = JsonSerializer.SerializeToElement(new { task_id = taskId }, JsonOpts),
            });
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
            using var pdoc = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());
            var row = pdoc.RootElement.GetProperty("data")[0];
            status = row.GetProperty("status").GetString();
            if (status is "completed" or "failed" or "cancelled") break;
        }

        Assert.Equal("completed", status);
    }

    [Fact]
    public async Task SystemTaskStatus_UnknownId_Returns404()
    {
        var resp = await PostInvoke(new ActionFrame
        {
            ActionId = ActionNodeMiddleware.SystemTaskStatus,
            Params   = JsonSerializer.SerializeToElement(new { task_id = "nope" }, JsonOpts),
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(NwpErrorCodes.TaskNotFound, body);
    }

    [Fact]
    public async Task SystemTaskStatus_MissingParam_Returns400()
    {
        var resp = await PostInvoke(new ActionFrame
        {
            ActionId = ActionNodeMiddleware.SystemTaskStatus,
            Params   = JsonSerializer.SerializeToElement(new { foo = "bar" }, JsonOpts),
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SystemTaskCancel_RunningTask_ReturnsOk()
    {
        _provider.AsyncCompletionDelay = TimeSpan.FromSeconds(10); // don't finish

        var start = await PostInvoke(new ActionFrame { ActionId = "orders.create", Async = true });
        var taskId = JsonDocument.Parse(await start.Content.ReadAsStringAsync())
            .RootElement.GetProperty("task_id").GetString()!;

        var cancel = await PostInvoke(new ActionFrame
        {
            ActionId = ActionNodeMiddleware.SystemTaskCancel,
            Params   = JsonSerializer.SerializeToElement(new { task_id = taskId }, JsonOpts),
        });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
    }

    [Fact]
    public async Task SystemTaskCancel_UnknownId_Returns404()
    {
        var resp = await PostInvoke(new ActionFrame
        {
            ActionId = ActionNodeMiddleware.SystemTaskCancel,
            Params   = JsonSerializer.SerializeToElement(new { task_id = "nope" }, JsonOpts),
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Idempotency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Invoke_Sync_IdempotencyKeyHit_ReturnsSameResult_NoReExecution()
    {
        _provider.SyncResult = JsonSerializer.SerializeToElement(new { call = 1 }, JsonOpts);
        var key   = Guid.NewGuid().ToString();
        var frame = new ActionFrame
        {
            ActionId       = "orders.ping",
            Params         = JsonSerializer.SerializeToElement(new { x = 1 }, JsonOpts),
            IdempotencyKey = key,
        };

        var first  = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, _provider.SyncInvocations);

        // Second call with same key must not re-execute
        _provider.SyncResult = JsonSerializer.SerializeToElement(new { call = 2 }, JsonOpts);
        var second = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, _provider.SyncInvocations); // still 1
    }

    [Fact]
    public async Task Invoke_Sync_IdempotencyConflict_Returns409()
    {
        var key = Guid.NewGuid().ToString();
        _provider.SyncResult = JsonSerializer.SerializeToElement(new { ok = true }, JsonOpts);

        var first = await PostInvoke(new ActionFrame
        {
            ActionId       = "orders.ping",
            Params         = JsonSerializer.SerializeToElement(new { x = 1 }, JsonOpts),
            IdempotencyKey = key,
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Same key, different params → conflict
        var second = await PostInvoke(new ActionFrame
        {
            ActionId       = "orders.ping",
            Params         = JsonSerializer.SerializeToElement(new { x = 2 }, JsonOpts),
            IdempotencyKey = key,
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains(NwpErrorCodes.ActionIdempotencyConflict, body);
    }

    // ── SSRF guard ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Invoke_Async_LoopbackCallback_Returns400()
    {
        var frame = new ActionFrame
        {
            ActionId    = "orders.create",
            Async       = true,
            CallbackUrl = "https://127.0.0.1/notify",
        };
        var resp = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(NwpErrorCodes.ActionParamsInvalid, body);
    }

    [Fact]
    public async Task Invoke_Async_NonHttpsCallback_Returns400()
    {
        var frame = new ActionFrame
        {
            ActionId    = "orders.create",
            Async       = true,
            CallbackUrl = "http://example.com/notify",
        };
        var resp = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Auth guard ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Invoke_RequireAuth_MissingHeader_Returns401()
    {
        using var authHost   = await BuildHost(requireAuth: true);
        using var authClient = authHost.GetTestClient();

        var frame   = new ActionFrame { ActionId = "orders.ping" };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var resp    = await authClient.PostAsync("/orders/invoke", content);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Invoke_RequireAuth_Present_Returns200()
    {
        _provider.SyncResult = JsonSerializer.SerializeToElement(new { ok = true }, JsonOpts);
        using var authHost   = await BuildHost(requireAuth: true);
        using var authClient = authHost.GetTestClient();

        var frame   = new ActionFrame { ActionId = "orders.ping" };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders/invoke")
        {
            Content = content,
            Headers = { { NwpHttpHeaders.Agent, "urn:nps:agent:ca.example.com:a1" } },
        };
        var resp = await authClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Path routing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownSubPath_PassesThrough()
    {
        var resp = await _client.GetAsync("/orders/unknown");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PathOutsidePrefix_PassesThrough()
    {
        var resp = await _client.GetAsync("/other");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Timeout enforcement ──────────────────────────────────────────────────

    [Fact]
    public async Task Invoke_Sync_ProviderTimeout_Returns504()
    {
        _provider.SyncDelay  = TimeSpan.FromSeconds(5);
        _provider.SyncResult = JsonSerializer.SerializeToElement(new { ok = true }, JsonOpts);

        var frame = new ActionFrame
        {
            ActionId  = "orders.ping",
            TimeoutMs = 100,
        };

        var resp = await PostInvoke(frame);
        Assert.Equal(HttpStatusCode.GatewayTimeout, resp.StatusCode);
    }

    [Fact]
    public async Task Invoke_Sync_ProviderThrows_Returns500()
    {
        _provider.ThrowSync = true;

        var frame = new ActionFrame { ActionId = "orders.ping" };
        var resp  = await PostInvoke(frame);

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostInvoke(ActionFrame frame)
    {
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        return _client.PostAsync("/orders/invoke", content);
    }

    private async Task<IHost> BuildHost(bool requireAuth)
    {
        var provider = _provider;
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton<FakeActionProvider>(_ => provider);
                    services.AddSingleton<IActionTaskStore, InMemoryActionTaskStore>();
                    services.AddSingleton<IIdempotencyCache, InMemoryIdempotencyCache>();
                });
                web.Configure(app =>
                {
                    app.UseActionNode<FakeActionProvider>(opts =>
                    {
                        opts.NodeId      = "urn:nps:node:test:orders";
                        opts.DisplayName = "Test Orders";
                        opts.PathPrefix  = "/orders";
                        opts.Actions     = Actions;
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

// ── Fake provider ────────────────────────────────────────────────────────────

internal sealed class FakeActionProvider : IActionNodeProvider
{
    public JsonElement? SyncResult { get; set; }
    public TimeSpan     SyncDelay  { get; set; } = TimeSpan.Zero;
    public bool         ThrowSync  { get; set; }
    public TimeSpan     AsyncCompletionDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    private int _syncInvocations;
    public int SyncInvocations => _syncInvocations;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame, ActionContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _syncInvocations);
        if (ThrowSync) throw new InvalidOperationException("forced failure");

        // Sync path uses SyncDelay; async path uses AsyncCompletionDelay.
        var delay = context.TaskId is null ? SyncDelay : AsyncCompletionDelay;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);

        return new ActionExecutionResult { Result = SyncResult };
    }
}
