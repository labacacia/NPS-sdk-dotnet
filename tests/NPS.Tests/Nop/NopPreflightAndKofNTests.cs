// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NOP;
using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using NPS.NOP.Storage;

namespace NPS.Tests.Nop;

// ── Helpers (file-local so they can reference file-local mock) ────────────────

file static class PkFixture
{
    public static (NopOrchestrator orch, PkMockWorkerClient worker) Build(
        Action<NopOrchestratorOptions>? configure = null)
    {
        var opts   = new NopOrchestratorOptions { ValidateSenderNid = false };
        configure?.Invoke(opts);
        var worker = new PkMockWorkerClient();
        var store  = new InMemoryNopTaskStore();
        var orch   = new NopOrchestrator(worker, store, opts);
        return (orch, worker);
    }

    public static TaskFrame SinglePreflight(string id, bool preflight = true) =>
        new()
        {
            TaskId   = Guid.NewGuid().ToString("D"),
            Preflight = preflight,
            Dag      = new TaskDag
            {
                Nodes = [new DagNode { Id = id, Action = $"nwp://node/{id}", Agent = id }],
                Edges = [],
            },
        };

    public static TaskFrame FanIn(
        string[] sources, string sink, uint minRequired = 0,
        bool preflight = false)
    {
        var nodes = sources
            .Select(s => new DagNode { Id = s, Action = $"nwp://node/{s}", Agent = s })
            .Append(new DagNode
            {
                Id          = sink,
                Action      = $"nwp://node/{sink}",
                Agent       = sink,
                InputFrom   = [..sources],
                MinRequired = minRequired,
            })
            .ToList();

        var edges = sources.Select(s => new DagEdge { From = s, To = sink }).ToList();

        return new TaskFrame
        {
            TaskId    = Guid.NewGuid().ToString("D"),
            Preflight = preflight,
            Dag       = new TaskDag { Nodes = nodes, Edges = edges },
        };
    }
}

file sealed class PkMockWorkerClient : INopWorkerClient
{
    private readonly Dictionary<string, string> _results = new();
    private readonly HashSet<string>            _fails   = new();

    public bool    PreflightAvailable         { get; set; } = true;
    public string? PreflightUnavailableReason { get; set; }

    public void SetupSuccess(string nodeId, string json) => _results[nodeId] = json;
    public void SetupFailure(string nodeId)              => _fails.Add(nodeId);

    public IAsyncEnumerable<AlignStreamFrame> DelegateAsync(DelegateFrame frame, CancellationToken ct = default)
        => Produce(frame.NodeId, ct);

    public Task<PreflightResult> PreflightAsync(
        string agentNid, string action, long estimatedNpt = 0,
        IReadOnlyList<string>? requiredCapabilities = null,
        CancellationToken ct = default) =>
        Task.FromResult(new PreflightResult
        {
            AgentNid          = agentNid,
            Available         = PreflightAvailable,
            UnavailableReason = PreflightUnavailableReason,
        });

    private async IAsyncEnumerable<AlignStreamFrame> Produce(
        string nodeId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var json = _results.TryGetValue(nodeId, out var j) ? j : "{}";
        bool fail = _fails.Contains(nodeId);
        yield return new AlignStreamFrame
        {
            StreamId  = Guid.NewGuid().ToString("D"),
            TaskId    = "t",
            SubtaskId = Guid.NewGuid().ToString("D"),
            Seq       = 0,
            IsFinal   = true,
            SenderNid = nodeId,
            Data      = fail ? null : JsonDocument.Parse(json).RootElement,
            Error     = fail
                ? new NPS.NOP.Models.StreamError { Code = "ERR", Message = "fail", Retryable = false }
                : null,
        };
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// Preflight tests
// ══════════════════════════════════════════════════════════════════════════════

public class NopPreflightTests
{
    // ── Preflight disabled ────────────────────────────────────────────────────

    [Fact]
    public async Task Preflight_Disabled_SkipsProbeAndExecutes()
    {
        var (orch, worker) = PkFixture.Build();
        worker.SetupSuccess("a", """{"done": true}""");

        var result = await orch.ExecuteAsync(PkFixture.SinglePreflight("a", preflight: false));

        Assert.Equal(TaskState.Completed, result.FinalState);
    }

    // ── All agents available ──────────────────────────────────────────────────

    [Fact]
    public async Task Preflight_AllAvailable_ExecutesDag()
    {
        var (orch, worker) = PkFixture.Build();
        worker.PreflightAvailable = true;
        worker.SetupSuccess("a", """{"done": true}""");

        var result = await orch.ExecuteAsync(PkFixture.SinglePreflight("a", preflight: true));

        Assert.Equal(TaskState.Completed, result.FinalState);
    }

    // ── Agent unavailable ─────────────────────────────────────────────────────

    [Fact]
    public async Task Preflight_AgentUnavailable_FailsWithResourceInsufficient()
    {
        var (orch, worker) = PkFixture.Build();
        worker.PreflightAvailable         = false;
        worker.PreflightUnavailableReason = "capacity_exceeded";

        var result = await orch.ExecuteAsync(PkFixture.SinglePreflight("a", preflight: true));

        Assert.Equal(TaskState.Failed, result.FinalState);
        Assert.Equal(NopErrorCodes.ResourceInsufficient, result.ErrorCode);
        Assert.Contains("capacity_exceeded", result.ErrorMessage);
    }

    // ── Multi-node preflight ──────────────────────────────────────────────────

    [Fact]
    public async Task Preflight_MultipleNodes_ProbesAllAgents()
    {
        var (orch, worker) = PkFixture.Build();
        worker.PreflightAvailable = true;
        worker.SetupSuccess("src",  """{"x": 1}""");
        worker.SetupSuccess("sink", """{"y": 2}""");

        var task = PkFixture.FanIn(["src"], "sink", preflight: true);
        var result = await orch.ExecuteAsync(task);

        // All agents available — execution should proceed
        Assert.Equal(TaskState.Completed, result.FinalState);
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// K-of-N tests
// ══════════════════════════════════════════════════════════════════════════════

public class NopKofNTests
{
    // ── All deps required (default, minRequired=0) ────────────────────────────

    [Fact]
    public async Task KofN_Default_AllRequired_OneFails_TaskFails()
    {
        var (orch, worker) = PkFixture.Build();
        worker.SetupSuccess("a", """{"v": 1}""");
        worker.SetupFailure("b");
        worker.SetupSuccess("sink", """{"done": true}""");

        // minRequired=0 → all of {a, b} must succeed
        var task = PkFixture.FanIn(["a", "b"], "sink", minRequired: 0);
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Failed, result.FinalState);
    }

    // ── K-of-N: 1-of-2 success is sufficient ─────────────────────────────────

    [Fact]
    public async Task KofN_OneOfTwo_OneFails_TaskSucceeds()
    {
        var (orch, worker) = PkFixture.Build();
        worker.SetupSuccess("a",    """{"v": 1}""");
        worker.SetupFailure("b");
        worker.SetupSuccess("sink", """{"done": true}""");

        // minRequired=1 → only 1 of {a, b} needs to succeed
        var task = PkFixture.FanIn(["a", "b"], "sink", minRequired: 1);
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
    }

    // ── K-of-N: 2-of-3, one failure still satisfies K ────────────────────────

    [Fact]
    public async Task KofN_TwoOfThree_OneFails_TaskSucceeds()
    {
        var (orch, worker) = PkFixture.Build();
        worker.SetupSuccess("a", """{"v": 1}""");
        worker.SetupSuccess("b", """{"v": 2}""");
        worker.SetupFailure("c");
        worker.SetupSuccess("sink", """{"done": true}""");

        var task = PkFixture.FanIn(["a", "b", "c"], "sink", minRequired: 2);
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
    }

    // ── K-of-N: 2-of-3 but 2 fail — K cannot be satisfied ───────────────────

    [Fact]
    public async Task KofN_TwoOfThree_TwoFail_TaskFails()
    {
        var (orch, worker) = PkFixture.Build();
        worker.SetupSuccess("a", """{"v": 1}""");
        worker.SetupFailure("b");
        worker.SetupFailure("c");
        worker.SetupSuccess("sink", """{"done": true}""");

        var task = PkFixture.FanIn(["a", "b", "c"], "sink", minRequired: 2);
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Failed, result.FinalState);
        Assert.Equal(NopErrorCodes.SyncDependencyFailed, result.ErrorCode);
    }

    // ── K-of-N: all succeed (N-of-N), should complete normally ───────────────

    [Fact]
    public async Task KofN_AllSucceed_TaskCompletes()
    {
        var (orch, worker) = PkFixture.Build();
        worker.SetupSuccess("a", """{"v": 1}""");
        worker.SetupSuccess("b", """{"v": 2}""");
        worker.SetupSuccess("c", """{"v": 3}""");
        worker.SetupSuccess("sink", """{"done": true}""");

        var task = PkFixture.FanIn(["a", "b", "c"], "sink", minRequired: 2);
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
    }

    // ── K-of-N: single source (degenerate case) ───────────────────────────────

    [Fact]
    public async Task KofN_SingleSource_MinRequired1_Succeeds()
    {
        var (orch, worker) = PkFixture.Build();
        worker.SetupSuccess("a",    """{"v": 1}""");
        worker.SetupSuccess("sink", """{"done": true}""");

        var task = PkFixture.FanIn(["a"], "sink", minRequired: 1);
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
    }

    [Fact]
    public async Task KofN_SingleSource_Fails_TaskFails()
    {
        var (orch, worker) = PkFixture.Build();
        worker.SetupFailure("a");
        worker.SetupSuccess("sink", """{"done": true}""");

        var task = PkFixture.FanIn(["a"], "sink", minRequired: 1);
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Failed, result.FinalState);
    }
}
