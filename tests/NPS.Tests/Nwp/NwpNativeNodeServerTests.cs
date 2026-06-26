// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Registry;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;
using NPS.NWP.MemoryNode;
using NPS.NWP.Native;

namespace NPS.Tests.Nwp;

public sealed class NwpNativeNodeServerTests
{
    [Fact]
    public async Task DispatchQuery_UsesMemoryProvider()
    {
        var server = new NwpNativeNodeServer(
            MakeCodec(),
            new NwpNativeNodeOptions
            {
                MemorySchema = new MemoryNodeSchema
                {
                    TableName = "products",
                    PrimaryKey = "id",
                    Fields = [new MemoryNodeField { Name = "id", Type = "int" }],
                },
                MemoryAnchorRef = "sha256:test",
                MemoryOptions = new MemoryNodeOptions
                {
                    NodeId = "urn:nps:node:test:memory",
                    PathPrefix = "/memory",
                    Schema = new MemoryNodeSchema
                    {
                        TableName = "products",
                        PrimaryKey = "id",
                        Fields = [new MemoryNodeField { Name = "id", Type = "int" }],
                    },
                },
            },
            memoryProvider: new StubMemoryProvider());

        var response = Assert.IsType<CapsFrame>(await server.DispatchAsync(new QueryFrame { Limit = 1 }));

        Assert.Equal("sha256:test", response.AnchorRef);
        Assert.Equal(1u, response.Count);
        Assert.Equal(1, response.Data[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task DispatchAction_UsesActionProvider()
    {
        var server = new NwpNativeNodeServer(
            MakeCodec(),
            new NwpNativeNodeOptions
            {
                ActionOptions = new ActionNodeOptions
                {
                    NodeId = "urn:nps:node:test:actions",
                    PathPrefix = "/actions",
                    Actions = new Dictionary<string, ActionSpec>
                    {
                        ["orders.ping"] = new() { Async = false, ResultAnchor = "sha256:result" },
                    },
                },
            },
            actionProvider: new StubActionProvider());

        var response = Assert.IsType<CapsFrame>(await server.DispatchAsync(
            new ActionFrame { ActionId = "orders.ping" }));

        Assert.Equal("sha256:result", response.AnchorRef);
        Assert.Equal("ok", response.Data[0].GetProperty("status").GetString());
    }

    private static NpsFrameCodec MakeCodec() =>
        new(new Tier1JsonCodec(), new Tier2MsgPackCodec(), FrameRegistry.CreateDefault());

    private sealed class StubMemoryProvider : IMemoryNodeProvider
    {
        public Task<MemoryNodeQueryResult> QueryAsync(
            QueryFrame frame,
            MemoryNodeSchema schema,
            MemoryNodeOptions options,
            CancellationToken ct = default) =>
            Task.FromResult(new MemoryNodeQueryResult
            {
                Rows = [new Dictionary<string, object?> { ["id"] = 1 }],
            });

        public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StreamAsync(
            QueryFrame frame,
            MemoryNodeSchema schema,
            MemoryNodeOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return [new Dictionary<string, object?> { ["id"] = 1 }];
            await Task.CompletedTask;
        }

        public Task<long> CountAsync(QueryFrame frame, MemoryNodeSchema schema, CancellationToken ct = default) =>
            Task.FromResult(1L);
    }

    private sealed class StubActionProvider : IActionNodeProvider
    {
        public Task<ActionExecutionResult> ExecuteAsync(
            ActionFrame frame,
            ActionContext context,
            CancellationToken ct = default)
        {
            using var doc = JsonDocument.Parse("""{"status":"ok"}""");
            return Task.FromResult(new ActionExecutionResult
            {
                Result = doc.RootElement.Clone(),
                TokenEst = 1,
            });
        }
    }
}
