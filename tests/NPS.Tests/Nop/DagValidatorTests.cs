// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NOP;
using NPS.NOP.Models;
using NPS.NOP.Validation;

namespace NPS.Tests.Nop;

public class DagValidatorTests
{
    [Fact]
    public void ValidLinearDag_Succeeds()
    {
        var dag = new TaskDag
        {
            Nodes =
            [
                new DagNode { Id = "a", Action = "nwp://x/op", Agent = "agent:a" },
                new DagNode { Id = "b", Action = "nwp://x/op", Agent = "agent:b", InputFrom = ["a"] },
                new DagNode { Id = "c", Action = "nwp://x/op", Agent = "agent:c", InputFrom = ["b"] },
            ],
            Edges =
            [
                new DagEdge { From = "a", To = "b" },
                new DagEdge { From = "b", To = "c" },
            ],
        };

        var result = DagValidator.Validate(dag);

        Assert.True(result.IsValid);
        Assert.NotNull(result.TopologicalOrder);
        Assert.Equal(3, result.TopologicalOrder.Count);
        Assert.Equal("a", result.TopologicalOrder[0]);
        Assert.Equal("c", result.TopologicalOrder[^1]);
    }

    [Fact]
    public void ValidDiamondDag_Succeeds()
    {
        var dag = new TaskDag
        {
            Nodes =
            [
                new DagNode { Id = "start", Action = "nwp://x/op", Agent = "a:1" },
                new DagNode { Id = "left",  Action = "nwp://x/op", Agent = "a:2", InputFrom = ["start"] },
                new DagNode { Id = "right", Action = "nwp://x/op", Agent = "a:3", InputFrom = ["start"] },
                new DagNode { Id = "end",   Action = "nwp://x/op", Agent = "a:4", InputFrom = ["left", "right"] },
            ],
            Edges =
            [
                new DagEdge { From = "start", To = "left" },
                new DagEdge { From = "start", To = "right" },
                new DagEdge { From = "left",  To = "end" },
                new DagEdge { From = "right", To = "end" },
            ],
        };

        var result = DagValidator.Validate(dag);

        Assert.True(result.IsValid);
        Assert.Equal("start", result.TopologicalOrder![0]);
        Assert.Equal("end", result.TopologicalOrder[^1]);
    }

    [Fact]
    public void SingleNodeDag_Succeeds()
    {
        var dag = new TaskDag
        {
            Nodes = [new DagNode { Id = "only", Action = "nwp://x/op", Agent = "a:1" }],
            Edges = [],
        };

        var result = DagValidator.Validate(dag);

        Assert.True(result.IsValid);
        Assert.Single(result.TopologicalOrder!);
    }

    [Fact]
    public void EmptyDag_Fails()
    {
        var dag = new TaskDag { Nodes = [], Edges = [] };
        var result = DagValidator.Validate(dag);

        Assert.False(result.IsValid);
        Assert.Equal(NopErrorCodes.TaskDagInvalid, result.ErrorCode);
    }

    [Fact]
    public void CyclicDag_Fails()
    {
        // a→b, b→c, c→b forms a cycle between b and c.
        // d is an end node (no outgoing), so both start/end checks pass.
        var dag = new TaskDag
        {
            Nodes =
            [
                new DagNode { Id = "a", Action = "nwp://x/op", Agent = "a:1" },
                new DagNode { Id = "b", Action = "nwp://x/op", Agent = "a:2" },
                new DagNode { Id = "c", Action = "nwp://x/op", Agent = "a:3" },
                new DagNode { Id = "d", Action = "nwp://x/op", Agent = "a:4" },
            ],
            Edges =
            [
                new DagEdge { From = "a", To = "b" },
                new DagEdge { From = "b", To = "c" },
                new DagEdge { From = "c", To = "b" },
                new DagEdge { From = "a", To = "d" },
            ],
        };

        var result = DagValidator.Validate(dag);

        Assert.False(result.IsValid);
        Assert.Equal(NopErrorCodes.TaskDagCycle, result.ErrorCode);
    }

    [Fact]
    public void TooManyNodes_Fails()
    {
        var nodes = Enumerable.Range(0, NopConstants.MaxDagNodes + 1)
            .Select(i => new DagNode { Id = $"n{i}", Action = "nwp://x/op", Agent = "a:1" })
            .ToList();

        var dag = new TaskDag { Nodes = nodes, Edges = [] };
        var result = DagValidator.Validate(dag);

        Assert.False(result.IsValid);
        Assert.Equal(NopErrorCodes.TaskDagTooLarge, result.ErrorCode);
    }

    [Fact]
    public void DuplicateNodeId_Fails()
    {
        var dag = new TaskDag
        {
            Nodes =
            [
                new DagNode { Id = "a", Action = "nwp://x/op", Agent = "a:1" },
                new DagNode { Id = "a", Action = "nwp://x/op2", Agent = "a:2" },
            ],
            Edges = [],
        };

        var result = DagValidator.Validate(dag);

        Assert.False(result.IsValid);
        Assert.Equal(NopErrorCodes.TaskDagInvalid, result.ErrorCode);
        Assert.Contains("Duplicate", result.ErrorMessage);
    }

    [Fact]
    public void EdgeReferencesUnknownNode_Fails()
    {
        var dag = new TaskDag
        {
            Nodes = [new DagNode { Id = "a", Action = "nwp://x/op", Agent = "a:1" }],
            Edges = [new DagEdge { From = "a", To = "ghost" }],
        };

        var result = DagValidator.Validate(dag);

        Assert.False(result.IsValid);
        Assert.Equal(NopErrorCodes.TaskDagInvalid, result.ErrorCode);
        Assert.Contains("ghost", result.ErrorMessage);
    }

    [Fact]
    public void InputFromReferencesUnknownNode_Fails()
    {
        var dag = new TaskDag
        {
            Nodes =
            [
                new DagNode { Id = "a", Action = "nwp://x/op", Agent = "a:1" },
                new DagNode { Id = "b", Action = "nwp://x/op", Agent = "a:2", InputFrom = ["nonexistent"] },
            ],
            Edges = [new DagEdge { From = "a", To = "b" }],
        };

        var result = DagValidator.Validate(dag);

        Assert.False(result.IsValid);
        Assert.Contains("nonexistent", result.ErrorMessage);
    }

    [Fact]
    public void NoStartNode_Fails()
    {
        var dag = new TaskDag
        {
            Nodes =
            [
                new DagNode { Id = "a", Action = "nwp://x/op", Agent = "a:1" },
                new DagNode { Id = "b", Action = "nwp://x/op", Agent = "a:2" },
            ],
            Edges =
            [
                new DagEdge { From = "a", To = "b" },
                new DagEdge { From = "b", To = "a" },
            ],
        };

        var result = DagValidator.Validate(dag);

        Assert.False(result.IsValid);
        // Cycle detected first (both have in-degree > 0 from the cycle)
    }

    [Fact]
    public void ConditionTooLong_Fails()
    {
        var longCondition = new string('x', NopConstants.MaxConditionLength + 1);
        var dag = new TaskDag
        {
            Nodes = [new DagNode { Id = "a", Action = "nwp://x/op", Agent = "a:1", Condition = longCondition }],
            Edges = [],
        };

        var result = DagValidator.Validate(dag);

        Assert.False(result.IsValid);
        Assert.Equal(NopErrorCodes.ConditionEvalError, result.ErrorCode);
    }

    [Fact]
    public void ExactlyMaxNodes_Succeeds()
    {
        var nodes = Enumerable.Range(0, NopConstants.MaxDagNodes)
            .Select(i => new DagNode { Id = $"n{i}", Action = "nwp://x/op", Agent = "a:1" })
            .ToList();

        var dag = new TaskDag { Nodes = nodes, Edges = [] };
        var result = DagValidator.Validate(dag);

        Assert.True(result.IsValid);
        Assert.Equal(NopConstants.MaxDagNodes, result.TopologicalOrder!.Count);
    }
}
