// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Frames;
using NPS.NOP.Frames;
using NPS.NOP.Models;

namespace NPS.Tests.Nop;

public class NopFrameTests
{
    [Fact]
    public void TaskFrame_FrameType_Is0x40()
    {
        var frame = CreateMinimalTaskFrame();
        Assert.Equal(FrameType.Task, frame.FrameType);
        Assert.Equal(0x40, (byte)frame.FrameType);
    }

    [Fact]
    public void TaskFrame_Defaults_AreCorrect()
    {
        var frame = CreateMinimalTaskFrame();
        Assert.Equal(30000u, frame.TimeoutMs);
        Assert.Equal(2, frame.MaxRetries);
        Assert.Equal(TaskPriority.Normal, frame.Priority);
        Assert.False(frame.Preflight);
        Assert.Null(frame.CallbackUrl);
        Assert.Null(frame.Context);
        Assert.Null(frame.RequestId);
    }

    [Fact]
    public void TaskFrame_WithContext_PreservesOtelFields()
    {
        var ctx = new TaskContext
        {
            TraceId = "4bf92f3577b34da6a3ce929d0e0e4736",
            SpanId = "00f067aa0ba902b7",
            TraceFlags = 1,
            SessionId = "sess-abc123",
        };

        var frame = CreateMinimalTaskFrame() with { Context = ctx };

        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", frame.Context!.TraceId);
        Assert.Equal("00f067aa0ba902b7", frame.Context.SpanId);
        Assert.Equal((byte)1, frame.Context.TraceFlags);
    }

    [Fact]
    public void DelegateFrame_FrameType_Is0x41()
    {
        var frame = CreateMinimalDelegateFrame();
        Assert.Equal(FrameType.Delegate, frame.FrameType);
        Assert.Equal(0x41, (byte)frame.FrameType);
    }

    [Fact]
    public void SyncFrame_FrameType_Is0x42()
    {
        var frame = new SyncFrame
        {
            TaskId = "task-1",
            SyncId = "sync-1",
            WaitFor = ["sub-a", "sub-b"],
        };

        Assert.Equal(FrameType.Sync, frame.FrameType);
        Assert.Equal(0x42, (byte)frame.FrameType);
    }

    [Fact]
    public void SyncFrame_MinRequired_DefaultsToZero()
    {
        var frame = new SyncFrame
        {
            TaskId = "task-1",
            SyncId = "sync-1",
            WaitFor = ["sub-a"],
        };

        Assert.Equal(0u, frame.MinRequired);
        Assert.Equal(AggregateStrategy.Merge, frame.Aggregate);
    }

    [Fact]
    public void AlignStreamFrame_FrameType_Is0x43()
    {
        var frame = new AlignStreamFrame
        {
            StreamId = "stream-1",
            TaskId = "task-1",
            SubtaskId = "sub-1",
            Seq = 0,
            IsFinal = false,
            SenderNid = "urn:nps:agent:example.com:worker",
        };

        Assert.Equal(FrameType.AlignStream, frame.FrameType);
        Assert.Equal(0x43, (byte)frame.FrameType);
    }

    [Fact]
    public void AlignStreamFrame_FinalWithError()
    {
        var error = new StreamError
        {
            Code = "NOP-TASK-TIMEOUT",
            Message = "Sub-task exceeded deadline",
            Retryable = false,
        };

        var frame = new AlignStreamFrame
        {
            StreamId = "stream-1",
            TaskId = "task-1",
            SubtaskId = "sub-1",
            Seq = 5,
            IsFinal = true,
            SenderNid = "urn:nps:agent:example.com:worker",
            Error = error,
        };

        Assert.True(frame.IsFinal);
        Assert.NotNull(frame.Error);
        Assert.Equal("NOP-TASK-TIMEOUT", frame.Error.Code);
        Assert.False(frame.Error.Retryable);
    }

    [Fact]
    public void AllNopFrames_PreferMsgPack()
    {
        IFrame[] frames =
        [
            CreateMinimalTaskFrame(),
            CreateMinimalDelegateFrame(),
            new SyncFrame { TaskId = "t", SyncId = "s", WaitFor = ["a"] },
            new AlignStreamFrame { StreamId = "s", TaskId = "t", SubtaskId = "st", Seq = 0, IsFinal = true, SenderNid = "n" },
        ];

        foreach (var frame in frames)
            Assert.Equal(EncodingTier.MsgPack, frame.PreferredTier);
    }

    [Fact]
    public void TaskFrame_RoundTripsViaJson()
    {
        var original = CreateMinimalTaskFrame() with
        {
            TimeoutMs = 60000,
            Preflight = true,
            Priority = TaskPriority.High,
            RequestId = "req-001",
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<TaskFrame>(json);

        Assert.NotNull(restored);
        Assert.Equal(original.TaskId, restored.TaskId);
        Assert.Equal(original.TimeoutMs, restored.TimeoutMs);
        Assert.Equal(original.Priority, restored.Priority);
        Assert.Equal(original.Preflight, restored.Preflight);
        Assert.Equal(original.Dag.Nodes.Count, restored.Dag.Nodes.Count);
    }

    private static TaskFrame CreateMinimalTaskFrame() => new()
    {
        TaskId = "550e8400-e29b-41d4-a716-446655440000",
        Dag = new TaskDag
        {
            Nodes = [new DagNode { Id = "step1", Action = "nwp://example.com/op", Agent = "urn:nps:agent:example.com:worker" }],
            Edges = [],
        },
    };

    private static DelegateFrame CreateMinimalDelegateFrame() => new()
    {
        ParentTaskId = "task-1",
        SubtaskId = "sub-1",
        NodeId = "fetch",
        TargetAgentNid = "urn:nps:agent:example.com:worker",
        Action = "nwp://example.com/products/query",
        DelegatedScope = JsonSerializer.SerializeToElement(new { nodes = new[] { "example.com" } }),
        DeadlineAt = "2026-04-14T12:00:00Z",
    };
}
