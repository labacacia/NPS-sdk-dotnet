// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NPS.NOP;
using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using NPS.NOP.Storage;
using NPS.NOP.Validation;
using TaskState = NPS.NOP.Models.TaskState;

namespace NPS.Tests.Nop;

/// <summary>
/// Tests for NPS-5 §8.2 (delegation chain depth), §8.4 (callback_url SSRF + retry).
/// </summary>
public sealed class NopHardeningTests
{
    // ── NopCallbackValidator ──────────────────────────────────────────────────

    [Theory]
    [InlineData("https://callback.example.com/nop/result",         null)]   // valid
    [InlineData("https://callback.example.com:8443/path?q=1",      null)]   // valid with port + query
    [InlineData("http://callback.example.com/nop",  "MUST use the https://")]   // not https
    [InlineData("ftp://callback.example.com/",      "MUST use the https://")]   // wrong scheme
    [InlineData("not-a-url",                        "is not a valid absolute URI")]
    [InlineData("",                                 "must not be empty")]
    public void ValidateCallbackUrl_ReturnsExpected(string url, string? expectedErrorFragment)
    {
        var result = NopCallbackValidator.ValidateCallbackUrl(url);

        if (expectedErrorFragment is null)
            Assert.Null(result);
        else
            Assert.Contains(expectedErrorFragment, result, StringComparison.OrdinalIgnoreCase);
    }

    // ── IsPrivateHost ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("localhost",       true)]
    [InlineData("LOCALHOST",       true)]
    [InlineData("127.0.0.1",       true)]
    [InlineData("127.0.0.100",     true)]
    [InlineData("10.0.0.1",        true)]
    [InlineData("10.255.255.255",  true)]
    [InlineData("172.16.0.1",      true)]
    [InlineData("172.31.255.255",  true)]
    [InlineData("172.15.0.1",      false)]  // just outside 172.16/12
    [InlineData("172.32.0.1",      false)]  // just outside 172.16/12
    [InlineData("192.168.0.1",     true)]
    [InlineData("169.254.1.1",     true)]   // link-local
    [InlineData("0.0.0.0",         true)]
    [InlineData("::1",             true)]   // IPv6 loopback
    [InlineData("8.8.8.8",         false)]  // public
    [InlineData("1.1.1.1",         false)]  // public
    [InlineData("callback.example.com", false)]  // hostname (no DNS)
    public void IsPrivateHost_ReturnsExpected(string host, bool expected)
    {
        Assert.Equal(expected, NopCallbackValidator.IsPrivateHost(host));
    }

    [Theory]
    [InlineData("https://127.0.0.1/hook",     "loopback")]
    [InlineData("https://10.0.0.5/hook",      "private")]
    [InlineData("https://192.168.1.1/hook",   "private")]
    [InlineData("https://172.20.0.1/hook",    "private")]
    [InlineData("https://169.254.0.1/hook",   "private")]
    [InlineData("https://localhost/hook",      "loopback")]
    public void ValidateCallbackUrl_PrivateIp_ReturnsError(string url, string _)
    {
        var result = NopCallbackValidator.ValidateCallbackUrl(url);
        Assert.NotNull(result);
        Assert.Contains("SSRF", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── ExecuteAsync: callback_url validation rejects bad URLs ────────────────

    [Fact]
    public async Task ExecuteAsync_InvalidCallbackUrl_FailsFast()
    {
        var orch = BuildOrchestrator(new SimpleWorkerClient());
        var task = SingleNodeTask() with { CallbackUrl = "http://callback.example.com/hook" };

        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Failed, result.FinalState);
        Assert.Contains("https://", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_PrivateIpCallbackUrl_FailsFast()
    {
        var orch = BuildOrchestrator(new SimpleWorkerClient());
        var task = SingleNodeTask() with { CallbackUrl = "https://10.0.0.1/hook" };

        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Failed, result.FinalState);
        Assert.Contains("SSRF", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ValidCallbackUrl_DoesNotReject()
    {
        // A public https:// URL must pass validation; skip actual callback HTTP call
        var worker = new SimpleWorkerClient();
        worker.SetupSuccess("n1", """{"ok":true}""");
        var orch = BuildOrchestrator(worker, opts => opts.EnableCallback = false);
        var task = SingleNodeTask() with { CallbackUrl = "https://callback.example.com/hook" };

        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
    }

    // ── Delegation chain depth ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DepthAtMax_Rejected()
    {
        var orch = BuildOrchestrator(new SimpleWorkerClient());
        var task = SingleNodeTask() with { DelegateDepth = NopConstants.MaxDelegateChainDepth };

        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Failed, result.FinalState);
        Assert.Equal(NopErrorCodes.DelegateChainTooDeep, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_DepthBelowMax_Accepted()
    {
        var worker = new SimpleWorkerClient();
        worker.SetupSuccess("n1", """{"ok":true}""");
        var orch = BuildOrchestrator(worker);
        var task = SingleNodeTask() with { DelegateDepth = NopConstants.MaxDelegateChainDepth - 1 };

        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
    }

    [Fact]
    public async Task ExecuteAsync_RootTask_HasDepthZero()
    {
        var worker = new SimpleWorkerClient();
        worker.SetupSuccess("n1", """{"ok":true}""");
        var orch = BuildOrchestrator(worker);
        var task = SingleNodeTask(); // DelegateDepth defaults to 0

        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
    }

    [Fact]
    public async Task DelegateFrame_CarriesIncrementedDepth()
    {
        // Verify the orchestrator increments DelegateDepth when building DelegateFrames
        DelegateFrame? capturedFrame = null;

        var worker = new CapturingWorkerClient(frame =>
        {
            capturedFrame = frame;
            return SingleFinalFrameAsync(frame.NodeId);
        });

        var store = new InMemoryNopTaskStore();
        var orch  = new NopOrchestrator(worker, store);

        var task = SingleNodeTask() with { DelegateDepth = 1 };
        await orch.ExecuteAsync(task);

        Assert.NotNull(capturedFrame);
        Assert.Equal(2, capturedFrame!.DelegateDepth); // 1 (task) + 1 = 2
    }

    // ── Callback retry ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CallbackFailsThenSucceeds_RetriesAndSucceeds()
    {
        int callCount = 0;
        var factory   = new StubHttpClientFactory(req =>
        {
            callCount++;
            // Fail first two attempts, succeed on third
            return callCount < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var worker = new SimpleWorkerClient();
        worker.SetupSuccess("n1", """{"ok":true}""");
        var orch = BuildOrchestrator(worker, opts => opts.EnableCallback = true, factory);

        var task = SingleNodeTask() with
        {
            CallbackUrl = "https://callback.example.com/hook",
        };

        var result = await orch.ExecuteAsync(task);
        Assert.Equal(TaskState.Completed, result.FinalState);

        // Wait a bit for the fire-and-forget callback to complete
        await Task.Delay(200);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_CallbackAlwaysFails_GivesUpAfterMaxRetries()
    {
        int callCount = 0;
        var factory   = new StubHttpClientFactory(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var worker = new SimpleWorkerClient();
        worker.SetupSuccess("n1", """{"ok":true}""");
        var orch = BuildOrchestrator(worker, opts => opts.EnableCallback = true, factory);

        var task   = SingleNodeTask() with { CallbackUrl = "https://callback.example.com/hook" };
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState); // task itself succeeded; callback failure is non-fatal
        await Task.Delay(500);       // give fire-and-forget time to finish retries
        Assert.Equal(NopConstants.CallbackMaxRetries, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_CallbackSucceedsFirstAttempt_NoDuplicateCalls()
    {
        int callCount = 0;
        var factory   = new StubHttpClientFactory(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var worker = new SimpleWorkerClient();
        worker.SetupSuccess("n1", """{"ok":true}""");
        var orch = BuildOrchestrator(worker, opts => opts.EnableCallback = true, factory);

        var task   = SingleNodeTask() with { CallbackUrl = "https://callback.example.com/hook" };
        await orch.ExecuteAsync(task);

        await Task.Delay(200);
        Assert.Equal(1, callCount); // only one call — no unnecessary retries
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TaskFrame SingleNodeTask() => new()
    {
        TaskId = Guid.NewGuid().ToString("D"),
        Dag    = new TaskDag
        {
            Nodes = [new DagNode { Id = "n1", Action = "nwp://node/n1", Agent = "n1" }],
            Edges = [],
        },
    };

    private static NopOrchestrator BuildOrchestrator(
        INopWorkerClient worker,
        Action<NopOrchestratorOptions>? configure = null,
        IHttpClientFactory? factory = null)
    {
        var opts = new NopOrchestratorOptions
        {
            EnableCallback           = true,
            CallbackRetryBaseDelayMs = 0,   // no real delays in tests
        };
        configure?.Invoke(opts);
        return new NopOrchestrator(worker, new InMemoryNopTaskStore(), opts,
            httpFactory: factory);
    }

    private static async IAsyncEnumerable<AlignStreamFrame> SingleFinalFrameAsync(
        string nodeId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return new AlignStreamFrame
        {
            StreamId  = Guid.NewGuid().ToString("D"),
            TaskId    = "task",
            SubtaskId = Guid.NewGuid().ToString("D"),
            Seq       = 0,
            IsFinal   = true,
            SenderNid = nodeId,
            Data      = JsonDocument.Parse("""{"ok":true}""").RootElement,
        };
    }
}

// ── HTTP stubs ────────────────────────────────────────────────────────────────

file sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
    public StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => _handler = handler;

    public HttpClient CreateClient(string name) =>
        new(new StubDelegatingHandler(_handler));
}

file sealed class StubDelegatingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
    public StubDelegatingHandler(Func<HttpRequestMessage, HttpResponseMessage> h) => _handler = h;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(_handler(request));
}

// ── Capturing worker (records DelegateFrames) ─────────────────────────────────

file sealed class CapturingWorkerClient : INopWorkerClient
{
    private readonly Func<DelegateFrame, IAsyncEnumerable<AlignStreamFrame>> _delegate;
    public CapturingWorkerClient(Func<DelegateFrame, IAsyncEnumerable<AlignStreamFrame>> d)
        => _delegate = d;

    public IAsyncEnumerable<AlignStreamFrame> DelegateAsync(
        DelegateFrame frame, CancellationToken ct = default) => _delegate(frame);

    public Task<PreflightResult> PreflightAsync(
        string agentNid, string action, long estimatedNpt = 0,
        IReadOnlyList<string>? requiredCapabilities = null,
        CancellationToken ct = default) =>
        Task.FromResult(new PreflightResult { AgentNid = agentNid, Available = true });
}

// ── Re-use MockWorkerClient from NopOrchestratorTests ─────────────────────────
// (defined as file-local there; re-define a minimal version here)

file sealed class SimpleWorkerClient : INopWorkerClient
{
    private readonly Dictionary<string, Func<DelegateFrame, IAsyncEnumerable<AlignStreamFrame>>> _handlers = new();

    public void SetupSuccess(string nodeId, string resultJson)
        => _handlers[nodeId] = _ => SuccessAsync(nodeId, resultJson);

    public IAsyncEnumerable<AlignStreamFrame> DelegateAsync(
        DelegateFrame frame, CancellationToken ct = default)
    {
        if (_handlers.TryGetValue(frame.NodeId, out var h)) return h(frame);
        return SuccessAsync(frame.NodeId, """{"ok":true}""");
    }

    public Task<PreflightResult> PreflightAsync(
        string agentNid, string action, long estimatedNpt = 0,
        IReadOnlyList<string>? requiredCapabilities = null,
        CancellationToken ct = default) =>
        Task.FromResult(new PreflightResult { AgentNid = agentNid, Available = true });

    private static async IAsyncEnumerable<AlignStreamFrame> SuccessAsync(
        string nodeId, string json,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return new AlignStreamFrame
        {
            StreamId  = Guid.NewGuid().ToString("D"),
            TaskId    = "task",
            SubtaskId = Guid.NewGuid().ToString("D"),
            Seq       = 0,
            IsFinal   = true,
            SenderNid = nodeId,
            Data      = JsonDocument.Parse(json).RootElement,
        };
    }
}
