// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using NPS.NOP;
using NPS.NOP.Storage;

namespace NPS.Tests.Nop;

// ── Mock worker client ────────────────────────────────────────────────────────

/// <summary>
/// Configurable mock: each node ID maps to a list of AlignStreamFrames to yield.
/// Optionally delays before returning (to simulate async work).
/// </summary>
file sealed class MockWorkerClient : INopWorkerClient
{
    private readonly Dictionary<string, Func<DelegateFrame, IAsyncEnumerable<AlignStreamFrame>>> _handlers = new();

    public void SetupSuccess(string nodeId, string resultJson, int delayMs = 0)
    {
        _handlers[nodeId] = _ => SingleFinalFrameAsync(nodeId, resultJson, delayMs: delayMs);
    }

    public void SetupFailure(string nodeId, string errorCode, string errorMsg = "")
    {
        _handlers[nodeId] = _ => FailFrameAsync(nodeId, errorCode, errorMsg);
    }

    public void SetupHandler(string nodeId, Func<DelegateFrame, IAsyncEnumerable<AlignStreamFrame>> handler)
        => _handlers[nodeId] = handler;

    // Preflight control
    public bool    PreflightAvailable        { get; set; } = true;
    public string? PreflightUnavailableReason { get; set; }

    public IAsyncEnumerable<AlignStreamFrame> DelegateAsync(DelegateFrame frame, CancellationToken ct = default)
    {
        if (_handlers.TryGetValue(frame.NodeId, out var handler))
            return handler(frame);

        return SingleFinalFrameAsync(frame.NodeId, """{"ok":true}""");
    }

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

    private static async IAsyncEnumerable<AlignStreamFrame> SingleFinalFrameAsync(
        string nodeId, string json, int delayMs = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (delayMs > 0) await Task.Delay(delayMs, ct);
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

    private static async IAsyncEnumerable<AlignStreamFrame> FailFrameAsync(
        string nodeId, string errorCode, string errorMsg,
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
            Error     = new StreamError { Code = errorCode, Message = errorMsg, Retryable = false },
        };
    }
}

// ── Test helpers ──────────────────────────────────────────────────────────────

/// <summary>Mutable counter safe to capture in async iterators (avoids CS1988 ref-in-async).</summary>
file sealed class CallCounter { public int Value; }

file static class TaskFrameBuilder
{
    public static TaskFrame Linear(params string[] nodeIds)
    {
        var nodes = nodeIds.Select((id, i) => new DagNode
        {
            Id        = id,
            Action    = $"nwp://node/{id}",
            Agent     = id,  // agent NID = node ID in tests (simplifies SenderNid matching)
            InputFrom = i == 0 ? null : [nodeIds[i - 1]],
        }).ToList();

        var edges = nodeIds.Zip(nodeIds.Skip(1), (a, b) => new DagEdge { From = a, To = b }).ToList();

        return new TaskFrame
        {
            TaskId = Guid.NewGuid().ToString("D"),
            Dag    = new TaskDag { Nodes = nodes, Edges = edges },
        };
    }

    public static TaskFrame Single(string id, string? condition = null) =>
        new()
        {
            TaskId = Guid.NewGuid().ToString("D"),
            Dag    = new TaskDag
            {
                Nodes = [new DagNode { Id = id, Action = $"nwp://node/{id}", Agent = id, Condition = condition }],
                Edges = [],
            },
        };
}

// ── Fixture (file-local so it can return file-local types) ────────────────────

file static class OrchestratorFixture
{
    public static (NopOrchestrator orch, MockWorkerClient worker) Build(
        Action<NopOrchestratorOptions>? configure = null)
    {
        var opts   = new NopOrchestratorOptions { ValidateSenderNid = false };
        configure?.Invoke(opts);
        var worker = new MockWorkerClient();
        var store  = new InMemoryNopTaskStore();
        var orch   = new NopOrchestrator(worker, store, opts);
        return (orch, worker);
    }

    public static async IAsyncEnumerable<AlignStreamFrame> FailThenSucceedAsync(
        string nodeId,
        CallCounter counter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        int c = System.Threading.Interlocked.Increment(ref counter.Value);
        await Task.CompletedTask;

        if (c == 1)
        {
            yield return new AlignStreamFrame
            {
                StreamId = Guid.NewGuid().ToString("D"), TaskId = "t", SubtaskId = Guid.NewGuid().ToString("D"),
                Seq = 0, IsFinal = true, SenderNid = nodeId,
                Error = new StreamError { Code = "ERR", Message = "", Retryable = true },
            };
        }
        else
        {
            yield return new AlignStreamFrame
            {
                StreamId = Guid.NewGuid().ToString("D"), TaskId = "t", SubtaskId = Guid.NewGuid().ToString("D"),
                Seq = 0, IsFinal = true, SenderNid = nodeId,
                Data = JsonDocument.Parse("""{"ok": true}""").RootElement,
            };
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public class NopOrchestratorTests
{

    // ── Happy paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SingleNode_Succeeds()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        worker.SetupSuccess("a", """{"value": 42}""");

        var result = await orch.ExecuteAsync(TaskFrameBuilder.Single("a"));

        Assert.Equal(TaskState.Completed, result.FinalState);
        Assert.True(result.NodeResults.ContainsKey("a"));
        Assert.Equal(42, result.NodeResults["a"].GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task LinearChain_ExecutesInOrder()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        var executionOrder = new List<string>();

        foreach (var id in new[] { "fetch", "analyze", "report" })
        {
            var captured = id;
            worker.SetupHandler(id, frame =>
            {
                executionOrder.Add(captured);
                return worker.DelegateAsync(new DelegateFrame
                {
                    ParentTaskId = frame.ParentTaskId, SubtaskId = frame.SubtaskId,
                    NodeId = captured, TargetAgentNid = captured,
                    Action = frame.Action, DelegatedScope = frame.DelegatedScope,
                    DeadlineAt = frame.DeadlineAt,
                }, CancellationToken.None);
            });
            worker.SetupSuccess(id, $$"""{"step": "{{id}}"}""");
        }

        var result = await orch.ExecuteAsync(TaskFrameBuilder.Linear("fetch", "analyze", "report"));

        Assert.Equal(TaskState.Completed, result.FinalState);
        Assert.Equal(3, result.NodeResults.Count);
    }

    [Fact]
    public async Task DiamondDag_BothBranchesComplete()
    {
        // start → left + right → end
        var (orch, worker) = OrchestratorFixture.Build();
        worker.SetupSuccess("start", """{"x": 1}""");
        worker.SetupSuccess("left",  """{"l": 10}""");
        worker.SetupSuccess("right", """{"r": 20}""");
        worker.SetupSuccess("end",   """{"done": true}""");

        var task = new TaskFrame
        {
            TaskId = Guid.NewGuid().ToString("D"),
            Dag    = new TaskDag
            {
                Nodes =
                [
                    new DagNode { Id = "start", Action = "nwp://x", Agent = "start" },
                    new DagNode { Id = "left",  Action = "nwp://x", Agent = "left",  InputFrom = ["start"] },
                    new DagNode { Id = "right", Action = "nwp://x", Agent = "right", InputFrom = ["start"] },
                    new DagNode { Id = "end",   Action = "nwp://x", Agent = "end",   InputFrom = ["left", "right"] },
                ],
                Edges =
                [
                    new DagEdge { From = "start", To = "left"  },
                    new DagEdge { From = "start", To = "right" },
                    new DagEdge { From = "left",  To = "end"   },
                    new DagEdge { From = "right", To = "end"   },
                ],
            },
        };
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
        Assert.Equal(4, result.NodeResults.Count);
    }

    [Fact]
    public async Task AggregatedResult_MergesEndNodeOutput()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        worker.SetupSuccess("a", """{"field_a": "hello"}""");
        worker.SetupSuccess("b", """{"field_b": "world"}""");

        var task = new TaskFrame
        {
            TaskId = Guid.NewGuid().ToString("D"),
            Dag    = new TaskDag
            {
                Nodes =
                [
                    new DagNode { Id = "a", Action = "nwp://x", Agent = "a" },
                    new DagNode { Id = "b", Action = "nwp://x", Agent = "b" },
                ],
                Edges = [],
            },
        };
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
        Assert.NotNull(result.AggregatedResult);
        Assert.True(result.AggregatedResult!.Value.TryGetProperty("field_a", out _));
        Assert.True(result.AggregatedResult!.Value.TryGetProperty("field_b", out _));
    }

    // ── Condition skip ────────────────────────────────────────────────────────

    [Fact]
    public async Task ConditionFalse_NodeSkipped()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        worker.SetupSuccess("fetch",  """{"count": 0}""");
        worker.SetupSuccess("report", """{"done": true}""");

        var task = new TaskFrame
        {
            TaskId = Guid.NewGuid().ToString("D"),
            Dag    = new TaskDag
            {
                Nodes =
                [
                    new DagNode { Id = "fetch",  Action = "nwp://x", Agent = "fetch" },
                    new DagNode { Id = "report", Action = "nwp://x", Agent = "report",
                                  InputFrom = ["fetch"], Condition = "$.fetch.count > 0" },
                ],
                Edges = [ new DagEdge { From = "fetch", To = "report" } ],
            },
        };
        var result = await orch.ExecuteAsync(task);

        // fetch completed; report skipped (count == 0)
        Assert.Equal(TaskState.Completed, result.FinalState);
        Assert.True(result.NodeResults.ContainsKey("fetch"));
        Assert.False(result.NodeResults.ContainsKey("report"));
    }

    [Fact]
    public async Task ConditionTrue_NodeExecutes()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        worker.SetupSuccess("fetch",  """{"count": 5}""");
        worker.SetupSuccess("report", """{"done": true}""");

        var task = new TaskFrame
        {
            TaskId = Guid.NewGuid().ToString("D"),
            Dag    = new TaskDag
            {
                Nodes =
                [
                    new DagNode { Id = "fetch",  Action = "nwp://x", Agent = "fetch" },
                    new DagNode { Id = "report", Action = "nwp://x", Agent = "report",
                                  InputFrom = ["fetch"], Condition = "$.fetch.count > 0" },
                ],
                Edges = [ new DagEdge { From = "fetch", To = "report" } ],
            },
        };
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
        Assert.True(result.NodeResults.ContainsKey("report"));
    }

    // ── Failure handling ──────────────────────────────────────────────────────

    [Fact]
    public async Task NodeFailure_TaskFails()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        worker.SetupFailure("fetch", "NOP-DELEGATE-REJECTED", "capacity exceeded");

        var result = await orch.ExecuteAsync(TaskFrameBuilder.Single("fetch"));

        Assert.Equal(TaskState.Failed, result.FinalState);
        Assert.NotNull(result.ErrorCode);
    }

    [Fact]
    public async Task NodeFailure_PropagatestoDependent()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        worker.SetupFailure("fetch", "NOP-DELEGATE-REJECTED");
        worker.SetupSuccess("analyze", """{"ok": true}""");

        var result = await orch.ExecuteAsync(TaskFrameBuilder.Linear("fetch", "analyze"));

        Assert.Equal(TaskState.Failed, result.FinalState);
    }

    // ── Retry ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retry_SucceedsOnSecondAttempt()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        var counter = new CallCounter();

        worker.SetupHandler("op", _ => OrchestratorFixture.FailThenSucceedAsync("op", counter));

        var task = new TaskFrame
        {
            TaskId = Guid.NewGuid().ToString("D"),
            MaxRetries = 2,
            Dag    = new TaskDag
            {
                Nodes = [new DagNode { Id = "op", Action = "nwp://x", Agent = "op",
                    RetryPolicy = new RetryPolicy { MaxRetries = 2, InitialDelayMs = 1 } }],
                Edges = [],
            },
        };
        var result = await orch.ExecuteAsync(task);

        Assert.Equal(TaskState.Completed, result.FinalState);
        Assert.Equal(2, counter.Value); // failed once, then succeeded
    }

    // ── DAG validation ────────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidDag_Cycle_ReturnsFailed()
    {
        var (orch, _) = OrchestratorFixture.Build();
        // DAG: s → a → b → a (cycle), a → e
        // s has in-degree 0 (start); e has out-degree 0 (end); a-b form a back-edge cycle.
        // This passes start/end-node checks and is caught by Kahn's algorithm.
        var task = new TaskFrame
        {
            TaskId = Guid.NewGuid().ToString("D"),
            Dag    = new TaskDag
            {
                Nodes =
                [
                    new DagNode { Id = "s", Action = "nwp://x", Agent = "s" },
                    new DagNode { Id = "a", Action = "nwp://x", Agent = "a" },
                    new DagNode { Id = "b", Action = "nwp://x", Agent = "b" },
                    new DagNode { Id = "e", Action = "nwp://x", Agent = "e" },
                ],
                Edges =
                [
                    new DagEdge { From = "s", To = "a" },
                    new DagEdge { From = "a", To = "b" },
                    new DagEdge { From = "b", To = "a" }, // back-edge — forms a→b→a cycle
                    new DagEdge { From = "a", To = "e" },
                ],
            },
        };
        var result = await orch.ExecuteAsync(task);
        Assert.Equal(TaskState.Failed, result.FinalState);
        Assert.Equal(NopErrorCodes.TaskDagCycle, result.ErrorCode);
    }

    [Fact]
    public async Task DuplicateTaskId_ReturnsFailed()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        worker.SetupSuccess("a", "{}");

        var task = TaskFrameBuilder.Single("a");
        await orch.ExecuteAsync(task);

        var result = await orch.ExecuteAsync(task); // same task_id
        Assert.Equal(TaskState.Failed, result.FinalState);
        Assert.Equal(NopErrorCodes.TaskAlreadyCompleted, result.ErrorCode);
    }

    // ── Status query ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_AfterExecution_ReturnsRecord()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        worker.SetupSuccess("a", "{}");

        var task = TaskFrameBuilder.Single("a");
        await orch.ExecuteAsync(task);

        var record = await orch.GetStatusAsync(task.TaskId);
        Assert.NotNull(record);
        Assert.Equal(task.TaskId, record!.TaskId);
        Assert.Equal(TaskState.Completed, record.State);
    }

    [Fact]
    public async Task GetStatus_UnknownId_ReturnsNull()
    {
        var (orch, _) = OrchestratorFixture.Build();
        Assert.Null(await orch.GetStatusAsync("no-such-id"));
    }

    // ── Timeout ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Timeout_FailsWithTimeoutCode()
    {
        var (orch, worker) = OrchestratorFixture.Build();
        worker.SetupSuccess("slow", "{}", delayMs: 5000); // 5 s

        var task = new TaskFrame
        {
            TaskId    = Guid.NewGuid().ToString("D"),
            TimeoutMs = 50, // 50 ms
            Dag       = new TaskDag
            {
                Nodes = [new DagNode { Id = "slow", Action = "nwp://x", Agent = "slow" }],
                Edges = [],
            },
        };
        var result = await orch.ExecuteAsync(task);
        Assert.Equal(TaskState.Failed, result.FinalState);
        Assert.Equal(NopErrorCodes.TaskTimeout, result.ErrorCode);
    }

}
