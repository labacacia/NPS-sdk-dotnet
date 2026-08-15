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
using NPS.Core;
using NPS.Core.Frames.Ncp;
using NPS.NWP.ActionNode;
using NPS.NWP.Actions;
using NPS.NWP.Extensions;
using NPS.NWP.Frames;
using NPS.NWP.Http;
using NPS.NWP.Llm;

namespace NPS.Tests.Nwp;

public sealed class StatefulLlmActionProviderTests : IAsyncLifetime
{
    private const string Alice = "urn:nps:agent:labacacia:alice";
    private const string Bob = "urn:nps:agent:labacacia:bob";

    private IHost _host = null!;
    private HttpClient _client = null!;
    private TestLlmProvider _inner = null!;
    private InMemoryLlmContextStore _store = null!;
    private StatefulLlmActionOptions _llmOptions = null!;

    public async Task InitializeAsync()
    {
        _inner = new TestLlmProvider();
        _store = new InMemoryLlmContextStore(new LlmContextStoreOptions
        {
            MaxContextsPerPrincipal = 7,
            MaxTtlSeconds = 900,
            TombstoneSeconds = 120,
            SupportedOperations = new HashSet<LlmContextOperation>
            {
                LlmContextOperation.Create,
                LlmContextOperation.Append,
                LlmContextOperation.Reset,
                LlmContextOperation.Release,
            },
        });
        _llmOptions = new StatefulLlmActionOptions("workspace-a", "runtime-1")
        {
            ProviderName = "willow",
            DefaultModel = "willow-small",
            SupportsStream = true,
            Authorizer = (_, _, _, _, _, _) => ValueTask.CompletedTask,
        };
        _host = await BuildHost();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Nwm_AdvertisesExactActionsAndProcessLimits()
    {
        var response = await _client.GetAsync("/llm/.nwm");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var profile = document.RootElement.GetProperty("profiles").GetProperty("llm");
        Assert.Equal("0.2", profile.GetProperty("profile_version").GetString());
        Assert.Equal("willow", profile.GetProperty("provider").GetString());
        Assert.True(profile.GetProperty("supports_stream").GetBoolean());
        var context = profile.GetProperty("context");
        Assert.Equal("process", context.GetProperty("persistence").GetString());
        Assert.Equal(7, context.GetProperty("max_contexts_per_principal").GetInt32());
        Assert.Equal(900, context.GetProperty("max_ttl_seconds").GetInt32());
        Assert.Equal(120, context.GetProperty("tombstone_seconds").GetInt32());
        Assert.Equal(
            ["create", "append", "reset", "release"],
            context.GetProperty("operations").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task SynchronousCreate_CommitsAndStatusRecoversIt()
    {
        var created = await Post(Alice, Complete(CreateRequest(), "create-1"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var completion = await Data(created);
        var receipt = completion.GetProperty("context");
        Assert.Equal(1UL, receipt.GetProperty("version").GetUInt64());
        Assert.Equal("create", receipt.GetProperty("operation").GetString());
        Assert.Equal("active", receipt.GetProperty("state").GetString());
        var contextId = receipt.GetProperty("context_id").GetString()!;

        var status = await Post(Alice, LlmContextActions.ToStatusActionFrame(
            new LlmContextStatusRequestDto { ContextId = contextId }));
        Assert.Equal("active", (await Data(status)).GetProperty("state").GetString());
        Assert.Equal(1, _inner.Calls);
    }

    [Fact]
    public async Task ReconnectConcurrentAppendAndProcessRestart_FollowContract()
    {
        var lost = await Post(Alice, Complete(CreateRequest(), "lost-create"));
        Assert.Equal(HttpStatusCode.OK, lost.StatusCode);

        using var reconnected = _host.GetTestClient();
        var recovered = await Data(await Post(reconnected, Alice,
            LlmContextActions.ToStatusActionFrame(
                new LlmContextStatusRequestDto { IdempotencyKey = "lost-create" })));
        Assert.Equal("active", recovered.GetProperty("state").GetString());
        Assert.Equal(1UL, recovered.GetProperty("version").GetUInt64());
        var contextId = recovered.GetProperty("context_id").GetString()!;
        var appendRequest = new LlmCompleteActionRequest
        {
            Model = "willow-small",
            Messages = [new LlmMessageDto { Role = "user", Content = "Two" }],
            Context = new LlmContextRequestDto
            {
                Operation = LlmContextOperation.Append,
                ContextId = contextId,
                BaseVersion = 1,
            },
        };
        _inner.Delay = TimeSpan.FromMilliseconds(200);
        var winnerTask = Post(reconnected, Alice, Complete(appendRequest, "append-winner"));
        for (var attempt = 0; attempt < 100 && _inner.Calls < 2; attempt++)
            await Task.Delay(5);
        Assert.Equal(2, _inner.Calls);
        var loser = await Post(reconnected, Alice, Complete(appendRequest, "append-loser"));
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Contains(NwpErrorCodes.LlmContextVersionConflict,
            await loser.Content.ReadAsStringAsync());
        var winner = await Data(await winnerTask);
        Assert.Equal(2UL, winner.GetProperty("context").GetProperty("version").GetUInt64());
        Assert.Equal(2, _inner.Calls);

        var originalStore = _store;
        var originalInner = _inner;
        _store = new InMemoryLlmContextStore(new LlmContextStoreOptions());
        _inner = new TestLlmProvider();
        var restartedHost = await BuildHost();
        try
        {
            using var restarted = restartedHost.GetTestClient();
            var appendAfterRestart = new LlmCompleteActionRequest
            {
                Model = "willow-small",
                Messages = [new LlmMessageDto { Role = "user", Content = "Three" }],
                Context = new LlmContextRequestDto
                {
                    Operation = LlmContextOperation.Append,
                    ContextId = contextId,
                    BaseVersion = 2,
                },
            };
            var missing = await Post(restarted, Alice,
                Complete(appendAfterRestart, "append-after-restart"));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Contains(NwpErrorCodes.LlmContextNotFound,
                await missing.Content.ReadAsStringAsync());
            Assert.Equal(0, _inner.Calls);
        }
        finally
        {
            await restartedHost.StopAsync();
            restartedHost.Dispose();
            _store = originalStore;
            _inner = originalInner;
        }
    }

    [Fact]
    public async Task AppendCommitsDelta_AndReleaseCreatesTombstone()
    {
        var contextId = (await Data(await Post(Alice, Complete(CreateRequest(), "create-1"))))
            .GetProperty("context").GetProperty("context_id").GetString()!;
        var appendRequest = new LlmCompleteActionRequest
        {
            Model = "willow-small",
            Messages = [new LlmMessageDto { Role = "user", Content = "Two" }],
            Context = new LlmContextRequestDto
            {
                Operation = LlmContextOperation.Append,
                ContextId = contextId,
                BaseVersion = 1,
            },
        };
        var appended = await Data(await Post(Alice, Complete(appendRequest, "append-1")));
        Assert.Equal(2UL, appended.GetProperty("context").GetProperty("version").GetUInt64());
        Assert.Equal(5, _store.Snapshot(Owner(Alice), contextId).Transcript.Count);

        var released = await Post(Alice, LlmContextActions.ToReleaseActionFrame(
            new LlmContextReleaseRequestDto { ContextId = contextId, BaseVersion = 2 },
            new NwpActionFrameOptions { IdempotencyKey = "release-1" }));
        var receipt = await Data(released);
        Assert.Equal("released", receipt.GetProperty("state").GetString());
        Assert.Equal(3UL, receipt.GetProperty("version").GetUInt64());
    }

    [Theory]
    [InlineData(TestProviderMode.Failure, HttpStatusCode.InternalServerError)]
    [InlineData(TestProviderMode.ModelError, HttpStatusCode.OK)]
    public async Task ProviderAndModelErrors_AbortWithoutAllocatingContext(
        TestProviderMode mode,
        HttpStatusCode expectedStatus)
    {
        _inner.Mode = mode;
        var key = mode.ToString();
        var response = await Post(Alice, Complete(CreateRequest(), key));
        Assert.Equal(expectedStatus, response.StatusCode);
        if (mode == TestProviderMode.ModelError)
            Assert.False((await Data(response)).TryGetProperty("context", out _));

        var status = await Data(await Post(Alice, LlmContextActions.ToStatusActionFrame(
            new LlmContextStatusRequestDto { IdempotencyKey = key })));
        Assert.Equal("failed", status.GetProperty("state").GetString());
        Assert.False(status.TryGetProperty("context_id", out _));
    }

    [Fact]
    public async Task CommitReauthorizationFailure_AbortsAndSurfacesAuthError()
    {
        _llmOptions.Authorizer = (_, _, stage, _, _, _) =>
        {
            if (stage == LlmAuthorizationStage.Commit)
            {
                throw new ActionNodeException(
                    401,
                    NpsStatusCodes.AuthUnauthenticated,
                    NwpErrorCodes.AuthNidRevoked,
                    "revoked before commit");
            }
            return ValueTask.CompletedTask;
        };
        var response = await Post(Alice, Complete(CreateRequest(), "revoked"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(NwpErrorCodes.AuthNidRevoked, await response.Content.ReadAsStringAsync());

        var status = await Data(await Post(Alice, LlmContextActions.ToStatusActionFrame(
            new LlmContextStatusRequestDto { IdempotencyKey = "revoked" })));
        Assert.Equal("failed", status.GetProperty("state").GetString());
        Assert.Equal(NwpErrorCodes.AuthNidRevoked, status.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task AsyncCompletion_PutsReceiptOnlyInTerminalTaskResult()
    {
        _inner.Delay = TimeSpan.FromMilliseconds(40);
        var accepted = await Post(Alice, Complete(CreateRequest(), "async-create", async: true));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var acceptedDoc = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.False(acceptedDoc.RootElement.TryGetProperty("context", out _));
        var taskId = acceptedDoc.RootElement.GetProperty("task_id").GetString()!;

        var task = await WaitForTerminal(Alice, taskId);
        Assert.Equal("completed", task.GetProperty("status").GetString());
        Assert.Equal(1UL, task.GetProperty("result").GetProperty("context")
            .GetProperty("version").GetUInt64());
    }

    [Fact]
    public async Task AsyncCancellation_AbortsReservationWithoutCommittingContext()
    {
        _inner.Delay = TimeSpan.FromSeconds(5);
        var accepted = await Post(Alice, Complete(CreateRequest(), "cancelled", async: true));
        using var acceptedDoc = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var taskId = acceptedDoc.RootElement.GetProperty("task_id").GetString()!;

        var cancelled = await Post(Alice, new ActionFrame
        {
            ActionId = ActionNodeMiddleware.SystemTaskCancel,
            Params = NwpActionPayloadCodec.ToJsonElement(new { task_id = taskId }),
        });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        LlmContextStatusDto? status = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(20);
            var response = await Post(Alice, LlmContextActions.ToStatusActionFrame(
                new LlmContextStatusRequestDto { IdempotencyKey = "cancelled" }));
            if (response.StatusCode != HttpStatusCode.OK) continue;
            status = NwpActionPayloadCodec.ReadJsonElement<LlmContextStatusDto>(await Data(response));
            if (status.State == LlmContextState.Failed) break;
        }
        Assert.NotNull(status);
        Assert.Equal(LlmContextState.Failed, status.State);
        Assert.Null(status.ContextId);
    }

    [Fact]
    public async Task StreamingCreate_CommitsAtTerminalAndReplaysWithFreshStreamId()
    {
        var frame = Complete(CreateRequest() with { Stream = true }, "stream-create");
        var first = await StreamFrames(await Post(Alice, frame));
        var replay = await StreamFrames(await Post(Alice, frame));

        Assert.Equal(2, first.Count);
        Assert.False(first[0].IsLast);
        Assert.True(first[1].IsLast);
        Assert.NotEqual(first[0].StreamId, replay[0].StreamId);
        Assert.All(first, item => Assert.Equal(first[0].StreamId, item.StreamId));
        Assert.All(replay, item => Assert.Equal(replay[0].StreamId, item.StreamId));

        var firstChunk = Assert.Single(LlmCompleteAction.ReadStreamChunks(first[0]));
        var terminal = Assert.Single(LlmCompleteAction.ReadStreamChunks(first[1]));
        var replayTerminal = Assert.Single(LlmCompleteAction.ReadStreamChunks(replay[1]));
        Assert.Null(firstChunk.Context);
        Assert.Equal("Fir", firstChunk.ContentDelta);
        Assert.Equal("st", terminal.ContentDelta);
        Assert.Equal(LlmStopReason.EndTurn, terminal.StopReason);
        Assert.NotNull(terminal.Context);
        Assert.Equal(terminal.Context, replayTerminal.Context);
        Assert.Equal(1UL, terminal.Context!.Version);
        Assert.Equal(1, _inner.Calls);

        var snapshot = _store.Snapshot(Owner(Alice), terminal.Context.ContextId);
        Assert.Equal("First", snapshot.Transcript[^1].Content);
    }

    [Fact]
    public async Task StreamingAbnormalEnd_AbortsAndEmitsTerminalProtocolError()
    {
        _inner.Mode = TestProviderMode.StreamAbnormal;
        var frames = await StreamFrames(await Post(
            Alice,
            Complete(CreateRequest() with { Stream = true }, "stream-abnormal")));

        Assert.Equal(2, frames.Count);
        Assert.False(frames[0].IsLast);
        Assert.True(frames[1].IsLast);
        Assert.Equal(NwpErrorCodes.NodeUnavailable, frames[1].ErrorCode);
        var status = await Data(await Post(Alice, LlmContextActions.ToStatusActionFrame(
            new LlmContextStatusRequestDto { IdempotencyKey = "stream-abnormal" })));
        Assert.Equal("failed", status.GetProperty("state").GetString());
        Assert.False(status.TryGetProperty("context_id", out _));
    }

    [Fact]
    public async Task StreamingCommitReauthorizationFailure_AbortsBeforeTerminalReceipt()
    {
        _llmOptions.Authorizer = (_, _, stage, _, _, _) =>
        {
            if (stage == LlmAuthorizationStage.Commit)
            {
                throw new ActionNodeException(
                    401,
                    NpsStatusCodes.AuthUnauthenticated,
                    NwpErrorCodes.AuthNidRevoked,
                    "revoked before stream commit");
            }
            return ValueTask.CompletedTask;
        };

        var frames = await StreamFrames(await Post(
            Alice,
            Complete(CreateRequest() with { Stream = true }, "stream-revoked")));
        Assert.Equal(2, frames.Count);
        Assert.Equal(NwpErrorCodes.AuthNidRevoked, frames[^1].ErrorCode);
        Assert.DoesNotContain(
            frames.SelectMany(LlmCompleteAction.ReadStreamChunks),
            chunk => chunk.Context is not null);

        var status = await Data(await Post(Alice, LlmContextActions.ToStatusActionFrame(
            new LlmContextStatusRequestDto { IdempotencyKey = "stream-revoked" })));
        Assert.Equal(NwpErrorCodes.AuthNidRevoked, status.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task StreamingDuplicateWhileLive_ReturnsConflictWithoutJoiningStream()
    {
        _inner.StreamGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = Complete(CreateRequest() with { Stream = true }, "stream-live");
        using var first = await Post(
            Alice,
            frame,
            HttpCompletionOption.ResponseHeadersRead);

        var duplicate = await Post(Alice, frame);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains(
            NwpErrorCodes.ActionIdempotencyConflict,
            await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(1, _inner.Calls);

        _inner.StreamGate.SetResult(true);
        var completed = await StreamFrames(first);
        Assert.True(completed[^1].IsLast);
    }

    [Fact]
    public async Task AsyncTaskStatusAndCancel_AreCallerScoped()
    {
        _inner.Delay = TimeSpan.FromSeconds(5);
        var accepted = await Post(Alice, Complete(CreateRequest(), "private-task", async: true));
        using var acceptedDoc = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var taskId = acceptedDoc.RootElement.GetProperty("task_id").GetString()!;
        var parameters = NwpActionPayloadCodec.ToJsonElement(new { task_id = taskId });

        var status = await Post(Bob, new ActionFrame
        {
            ActionId = ActionNodeMiddleware.SystemTaskStatus,
            Params = parameters,
        });
        var cancel = await Post(Bob, new ActionFrame
        {
            ActionId = ActionNodeMiddleware.SystemTaskCancel,
            Params = parameters,
        });
        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);

        var ownerCancel = await Post(Alice, new ActionFrame
        {
            ActionId = ActionNodeMiddleware.SystemTaskCancel,
            Params = parameters,
        });
        Assert.Equal(HttpStatusCode.OK, ownerCancel.StatusCode);
    }

    [Fact]
    public async Task ResponseIdempotency_IsOwnerScopedAndDoesNotRecommit()
    {
        var aliceFirst = await Data(await Post(Alice, Complete(CreateRequest(), "shared-key")));
        var aliceReplay = await Data(await Post(Alice, Complete(CreateRequest(), "shared-key")));
        var bobFirst = await Data(await Post(Bob, Complete(CreateRequest(), "shared-key")));
        var aliceId = aliceFirst.GetProperty("context").GetProperty("context_id").GetString();
        var replayId = aliceReplay.GetProperty("context").GetProperty("context_id").GetString();
        var bobId = bobFirst.GetProperty("context").GetProperty("context_id").GetString();
        Assert.Equal(aliceId, replayId);
        Assert.NotEqual(aliceId, bobId);
        Assert.Equal(2, _inner.Calls);
    }

    [Fact]
    public async Task CachedReplay_RechecksAuthorizationBeforeReturningResult()
    {
        var admitted = true;
        _llmOptions.Authorizer = (_, _, stage, _, _, _) =>
        {
            if (stage == LlmAuthorizationStage.Admission && !admitted)
            {
                throw new ActionNodeException(
                    401,
                    NpsStatusCodes.AuthUnauthenticated,
                    NwpErrorCodes.AuthNidRevoked,
                    "caller was revoked");
            }
            return ValueTask.CompletedTask;
        };
        Assert.Equal(HttpStatusCode.OK,
            (await Post(Alice, Complete(CreateRequest(), "cached"))).StatusCode);
        admitted = false;
        var replay = await Post(Alice, Complete(CreateRequest(), "cached"));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(1, _inner.Calls);
    }

    [Fact]
    public async Task AuthorizationReceivesExactCapabilities_AndFailsClosedWhenMissing()
    {
        var checks = new List<(string Action, LlmAuthorizationStage Stage, string[] Capabilities)>();
        _llmOptions.Authorizer = (_, action, stage, capabilities, _, _) =>
        {
            checks.Add((action, stage, capabilities.ToArray()));
            return ValueTask.CompletedTask;
        };

        Assert.Equal(HttpStatusCode.OK,
            (await Post(Alice, Complete(CreateRequest(), "capabilities"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Post(Alice, LlmContextActions.ToStatusActionFrame(
                new LlmContextStatusRequestDto { IdempotencyKey = "capabilities" }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await Post(Alice, Complete(CreateRequest() with
            {
                Stream = true,
                Tools = [new LlmToolDefinitionDto { Name = "lookup" }],
            }, "extended-capabilities"))).StatusCode);

        Assert.Collection(
            checks,
            check => Assert.Equal(
                [LlmCompleteAction.CapabilityComplete, LlmCompleteAction.CapabilityContext],
                check.Capabilities),
            check => Assert.Equal(
                [LlmCompleteAction.CapabilityComplete, LlmCompleteAction.CapabilityContext],
                check.Capabilities),
            check => Assert.Equal([LlmCompleteAction.CapabilityContext], check.Capabilities),
            check => Assert.Equal(
                [
                    LlmCompleteAction.CapabilityComplete,
                    LlmCompleteAction.CapabilityContext,
                    LlmCompleteAction.CapabilityStream,
                    LlmCompleteAction.CapabilityToolCall,
                ],
                check.Capabilities));

        _llmOptions.Authorizer = null;
        var denied = await Post(Alice, Complete(CreateRequest(), "no-authorizer"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains(NwpErrorCodes.LlmContextForbidden, await denied.Content.ReadAsStringAsync());
        Assert.Equal(1, _inner.Calls);
    }

    [Fact]
    public async Task MalformedStatefulRequests_FailBeforeProviderDispatch()
    {
        var missingKey = Complete(CreateRequest(), key: null);
        var streamedAsync = Complete(CreateRequest() with { Stream = true }, "streamed", async: true);
        var tools = Complete(CreateRequest() with
        {
            Tools = [new LlmToolDefinitionDto { Name = "lookup" }],
        }, "tools");
        var resetWithoutVersion = Complete(CreateRequest() with
        {
            Context = new LlmContextRequestDto { Operation = LlmContextOperation.Reset },
        }, "reset-without-version");

        foreach (var frame in new[] { missingKey, streamedAsync, tools, resetWithoutVersion })
        {
            var response = await Post(Alice, frame);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains(NwpErrorCodes.ActionParamsInvalid, await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, _inner.Calls);
    }

    [Fact]
    public async Task LifecycleActions_RequireAuthenticationAndOwner()
    {
        var unauthenticated = await Post(null, LlmContextActions.ToStatusActionFrame(
            new LlmContextStatusRequestDto { ContextId = "AQIDBAUGBwgJCgsMDQ4PEA" }));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var contextId = (await Data(await Post(Alice, Complete(CreateRequest(), "owned"))))
            .GetProperty("context").GetProperty("context_id").GetString()!;
        var forbidden = await Post(Bob, LlmContextActions.ToStatusActionFrame(
            new LlmContextStatusRequestDto { ContextId = contextId }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains(NwpErrorCodes.LlmContextForbidden, await forbidden.Content.ReadAsStringAsync());
    }

    private async Task<IHost> BuildHost()
    {
        var coordinator = new StatefulLlmActionProvider(_inner, _store, _llmOptions);
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton(coordinator);
                    services.AddSingleton<IActionTaskStore, InMemoryActionTaskStore>();
                    services.AddSingleton<IActionTaskCancellationRegistry, InMemoryActionTaskCancellationRegistry>();
                    services.AddSingleton<IIdempotencyCache, InMemoryIdempotencyCache>();
                });
                web.Configure(app =>
                {
                    app.UseActionNode<StatefulLlmActionProvider>(options =>
                    {
                        options.NodeId = "urn:nps:node:llm.example:willow";
                        options.PathPrefix = "/llm";
                        coordinator.ConfigureNode(options);
                    });
                });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private async Task<HttpResponseMessage> Post(
        string? agent,
        ActionFrame frame,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        => await Post(_client, agent, frame, completion);

    private static async Task<HttpResponseMessage> Post(
        HttpClient client,
        string? agent,
        ActionFrame frame,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(frame, NwpActionPayloadCodec.JsonOptions),
            Encoding.UTF8,
            NwpHttpHeaders.MimeFrame);
        var request = new HttpRequestMessage(HttpMethod.Post, "/llm/invoke") { Content = content };
        if (agent is not null) request.Headers.Add(NwpHttpHeaders.Agent, agent);
        return await client.SendAsync(request, completion);
    }

    private static ActionFrame Complete(
        LlmCompleteActionRequest request,
        string? key,
        bool async = false) =>
        LlmCompleteAction.ToActionFrame(request, new NwpActionFrameOptions
        {
            IdempotencyKey = key,
            Async = async,
            RequestId = Guid.NewGuid().ToString(),
        });

    private static LlmCompleteActionRequest CreateRequest() => new()
    {
        Model = "willow-small",
        Messages =
        [
            new LlmMessageDto { Role = "system", Content = "Be concise." },
            new LlmMessageDto { Role = "user", Content = "One" },
        ],
        Context = new LlmContextRequestDto
        {
            Operation = LlmContextOperation.Create,
            TtlSeconds = 600,
        },
    };

    private async Task<JsonElement> WaitForTerminal(string agent, string taskId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(20);
            var response = await Post(agent, new ActionFrame
            {
                ActionId = ActionNodeMiddleware.SystemTaskStatus,
                Params = NwpActionPayloadCodec.ToJsonElement(new { task_id = taskId }),
            });
            var task = await Data(response);
            if (task.GetProperty("status").GetString() is "completed" or "failed" or "cancelled")
                return task;
        }
        throw new TimeoutException("Async action did not reach a terminal state.");
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data")[0].Clone();
    }

    private static async Task<IReadOnlyList<StreamFrame>> StreamFrames(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var lines = (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select(line =>
            JsonSerializer.Deserialize<StreamFrame>(line, NwpActionPayloadCodec.JsonOptions)!)
            .ToArray();
    }

    private static LlmContextOwner Owner(string nid) => new(nid, "workspace-a");
}

public enum TestProviderMode
{
    Success,
    Failure,
    ModelError,
    StreamAbnormal,
}

internal sealed class TestLlmProvider : IActionNodeProvider
{
    private int _calls;

    public TestProviderMode Mode { get; set; }
    public TimeSpan Delay { get; set; }
    public TaskCompletionSource<bool>? StreamGate { get; set; }
    public int Calls => _calls;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame,
        ActionContext context,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Mode == TestProviderMode.Failure) throw ActionNodeException.Internal("provider failed");

        var request = LlmCompleteAction.ReadRequest(frame);
        if (request.Stream)
        {
            return new ActionExecutionResult
            {
                StreamFrames = Stream(Mode, ct),
                TokenEst = 1,
            };
        }

        var response = Mode == TestProviderMode.ModelError
            ? new LlmCompleteActionResponse
            {
                StopReason = LlmStopReason.Error,
                Error = "model unavailable",
                Context = new LlmContextReceiptDto
                {
                    ContextId = "AQIDBAUGBwgJCgsMDQ4PEA",
                    Version = 99,
                    Operation = LlmContextOperation.Create,
                    State = LlmContextState.Active,
                },
            }
            : new LlmCompleteActionResponse
            {
                StopReason = LlmStopReason.EndTurn,
                Content = "First",
                Usage = new LlmUsageDto
                {
                    InputTokens = 2,
                    OutputTokens = 1,
                    WireInputBytes = context.WireInputBytes,
                },
            };
        return new ActionExecutionResult
        {
            Result = LlmCompleteAction.ToResponsePayload(response),
            TokenEst = 1,
        };
    }

    private async IAsyncEnumerable<StreamFrame> Stream(
        TestProviderMode mode,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return LlmCompleteAction.ToStreamFrame(
            "provider-stream",
            0,
            false,
            [new LlmCompleteStreamChunkDto { ContentDelta = "Fir" }],
            includeAnchorRef: true);
        if (StreamGate is not null) await StreamGate.Task.WaitAsync(ct);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (mode == TestProviderMode.StreamAbnormal) yield break;
        yield return LlmCompleteAction.ToStreamFrame(
            "provider-stream",
            1,
            true,
            [new LlmCompleteStreamChunkDto
            {
                ContentDelta = "st",
                StopReason = LlmStopReason.EndTurn,
                Usage = new LlmUsageDto { InputTokens = 2, OutputTokens = 1 },
            }]);
    }
}
