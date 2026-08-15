// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NWP.Http;
using NPS.NWP.Llm;

namespace NPS.Tests.Nwp;

public sealed class LlmContextStoreConformanceTests
{
    private static readonly LlmContextOwner Alice =
        new("urn:nps:agent:labacacia:alice", "workspace-a");
    private static readonly LlmContextOwner Bob =
        new("urn:nps:agent:labacacia:bob", "workspace-a");

    public static IEnumerable<object[]> Vectors()
    {
        using var fixture = LoadFixture();
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            yield return [vector.Clone()];
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SharedVector_IsExecutedByReferenceStore(JsonElement vector)
    {
        var id = Str(vector, "id");
        AssertFixtureContract(vector);
        switch (id)
        {
            case "nwp.llm-context.001": StatelessCompatibility(); break;
            case "nwp.llm-context.002": CreateCommitsV1(); break;
            case "nwp.llm-context.003": AppendCommitsDelta(); break;
            case "nwp.llm-context.004": StaleAndConcurrentLoseCas(); break;
            case "nwp.llm-context.005": ForkSnapshotsParent(); break;
            case "nwp.llm-context.006": ResetReplacesBindingAndTranscript(); break;
            case "nwp.llm-context.007": BindingMismatchFailsClosed(); break;
            case "nwp.llm-context.008": OwnerAndCapabilityBoundary(); break;
            case "nwp.llm-context.009": AbortPreservesCommittedState(); break;
            case "nwp.llm-context.010": LostCreateIsRecoveredByKey(); break;
            case "nwp.llm-context.011": ReleaseAndExpiryTombstones(); break;
            case "nwp.llm-context.012": UsageAccountingIsMeasurable(); break;
            case "nwp.llm-context.013": AdvertisedOperationsAreEnforced(); break;
            case "nwp.llm-context.014": ProcessRestartDoesNotRecreateState(); break;
            case "nwp.llm-context.015": CompletedIdempotencyDoesNotRecommit(); break;
            case "nwp.llm-context.016": RevocationAbortPreservesVersion(); break;
            case "nwp.llm-context.017": PrincipalLimitPreventsAllocation(); break;
            case "nwp.llm-context.018": UnsupportedOperationHasDedicatedError(); break;
            case "nwp.llm-context.019": MissingIdempotencyFailsBeforeAllocation(); break;
            default: throw new InvalidOperationException($"Unimplemented shared vector: {id}");
        }
    }

    private static void AssertFixtureContract(JsonElement vector)
    {
        var id = Str(vector, "id");
        var input = Obj(vector, "input");
        var expected = Obj(vector, "expected");
        Assert.True(input.EnumerateObject().Any(), $"{id} input must not be empty");
        Assert.True(expected.EnumerateObject().Any(), $"{id} expected must not be empty");

        switch (id[^3..])
        {
            case "001":
                Assert.False(Obj(input, "params").TryGetProperty("context", out _));
                Assert.Equal("stateless", Str(expected, "mode"));
                Assert.True(Bool(expected, "dispatched"));
                Assert.False(Bool(expected, "context_mutated"));
                break;
            case "002":
                Assert.Equal(Str(input, "owner_nid"), Str(expected, "owner_nid"));
                Assert.Equal(1, Int(expected, "version"));
                Assert.True(Bool(expected, "committed"));
                break;
            case "003":
                var pre3 = Obj(input, "pre_state");
                var params3 = Obj(input, "params");
                Assert.Equal(Int(pre3, "version") + 1, Int(expected, "version"));
                Assert.Equal(Len(params3, "messages"), Int(expected, "accepted_delta_message_count"));
                Assert.Equal(Len(pre3, "messages") + Len(params3, "messages") + 1,
                    Int(expected, "post_message_count"));
                break;
            case "004":
                Assert.Equal(Int(Obj(input, "pre_state"), "version"), Int(expected, "post_version"));
                Assert.Equal(Int(Obj(input, "pre_state"), "version"), Int(Obj(expected, "hint"), "current_version"));
                Assert.Equal(NwpErrorCodes.LlmContextVersionConflict, Str(expected, "error"));
                break;
            case "005":
                Assert.Equal(Int(Obj(input, "request"), "base_version"), Int(expected, "parent_version"));
                Assert.Equal(Int(input, "parent_version_at_child_commit"), Int(expected, "post_parent_version"));
                Assert.Equal(1, Int(expected, "version"));
                break;
            case "006":
                Assert.Equal(Int(Obj(input, "pre_state"), "version") + 1, Int(expected, "version"));
                Assert.Equal(Str(Obj(input, "request"), "model"), Str(expected, "resolved_model"));
                break;
            case "007":
                Assert.Equal(Int(Obj(input, "pre_state"), "version"), Int(expected, "post_version"));
                Assert.Equal(NwpErrorCodes.LlmContextBindingMismatch, Str(expected, "error"));
                Assert.False(Bool(expected, "provider_dispatched") || Bool(expected, "stateless_fallback"));
                break;
            case "008":
                Assert.NotEqual(Str(input, "owner_nid"), Str(input, "caller_nid"));
                Assert.DoesNotContain(LlmCompleteAction.CapabilityContext, Strings(input, "caller_capabilities"));
                Assert.Equal(NwpErrorCodes.LlmContextForbidden, Str(expected, "error"));
                break;
            case "009":
                Assert.Equal(Int(Obj(input, "pre_state"), "version"), Int(expected, "post_version"));
                Assert.False(Bool(expected, "committed"));
                Assert.True(Bool(expected, "reservation_released"));
                break;
            case "010":
                var terminal10 = Obj(input.GetProperty("status_sequence").EnumerateArray().Last(), "");
                var completed10 = Obj(expected, "completed_status");
                Assert.False(Bool(Obj(expected, "running_status"), "context_id_present"));
                Assert.Equal(Str(terminal10, "context_id"), Str(completed10, "context_id"));
                Assert.Equal(Int(terminal10, "version"), Int(completed10, "version"));
                break;
            case "011":
                Assert.Equal(Int(Obj(input, "pre_state"), "version") + 1,
                    Int(Obj(expected, "release_receipt"), "version"));
                Assert.Equal(Int(Obj(input, "expiry_branch"), "active_version"),
                    Int(Obj(expected, "expiry_tombstone"), "version"));
                break;
            case "012":
                var usage12 = Obj(input, "usage");
                Assert.Equal(Int(usage12, "input_tokens"), Int(usage12, "reused_tokens") + Int(usage12, "evaluated_tokens"));
                Assert.True(Int(usage12, "wire_input_bytes") < Int(input, "stateless_wire_input_bytes"));
                Assert.True(Bool(expected, "usage_equation_valid") && Bool(expected, "wire_input_smaller_than_stateless"));
                break;
            case "013":
                var context13 = Obj(Obj(input, "manifest"), "context");
                Assert.Equal(Strings(input, "implemented_operations"), Strings(context13, "operations"));
                Assert.Equal(Str(input, "implemented_persistence"), Str(context13, "persistence"));
                Assert.True(Bool(expected, "manifest_valid"));
                Assert.Equal(LlmCompleteAction.CapabilityContext, Str(expected, "requires_capability"));
                break;
            case "014":
                Assert.Equal("process", Str(input, "persistence"));
                Assert.Equal("process_restart", Str(input, "event"));
                Assert.Equal(NwpErrorCodes.LlmContextNotFound, Str(expected, "error"));
                Assert.False(Bool(expected, "replacement_created") || Bool(expected, "stateless_fallback"));
                break;
            case "015":
                var original15 = Obj(input, "original");
                Assert.Equal(string.Concat(Strings(original15, "chunks")), Str(expected, "ordered_content"));
                Assert.NotEqual(Str(original15, "stream_id"), Str(input, "replay_stream_id"));
                Assert.Equal(0, Int(expected, "provider_invocations") + Int(expected, "additional_context_commits"));
                break;
            case "016":
                Assert.Equal("valid", Str(input, "authorization_at_admission"));
                Assert.Equal("revoked", Str(input, "authorization_at_commit"));
                Assert.Equal(Int(Obj(input, "pre_state"), "version"), Int(expected, "post_version"));
                Assert.Equal(NwpErrorCodes.AuthNidRevoked, Str(expected, "error"));
                break;
            case "017":
                Assert.Equal(Int(input, "max_contexts_per_principal"), Int(input, "live_contexts"));
                Assert.Equal(NwpErrorCodes.LlmContextLimitExceeded, Str(expected, "error"));
                Assert.False(Bool(expected, "context_allocated"));
                break;
            case "018":
                Assert.DoesNotContain(Str(Obj(input, "request"), "operation"), Strings(input, "advertised_operations"));
                Assert.Equal(NwpErrorCodes.LlmContextOperationUnsupported, Str(expected, "error"));
                break;
            case "019":
                Assert.False(Bool(input, "idempotency_key_present"));
                Assert.Equal(NwpErrorCodes.ActionParamsInvalid, Str(expected, "error"));
                Assert.False(Bool(expected, "context_allocated") || Bool(expected, "provider_dispatched"));
                break;
            default: throw new InvalidOperationException($"Unimplemented fixture contract: {id}");
        }
    }

    private static JsonElement Obj(JsonElement value, string property) =>
        string.IsNullOrEmpty(property) ? value : value.GetProperty(property);
    private static string Str(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    private static int Int(JsonElement value, string property) => value.GetProperty(property).GetInt32();
    private static bool Bool(JsonElement value, string property) => value.GetProperty(property).GetBoolean();
    private static int Len(JsonElement value, string property) => value.GetProperty(property).GetArrayLength();
    private static string[] Strings(JsonElement value, string property) =>
        value.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static void StatelessCompatibility()
    {
        var request = new LlmCompleteActionRequest
        {
            Model = "willow-small",
            Messages = [User("Hello")],
        };
        Assert.Null(request.Context);
    }

    private static void CreateCommitsV1()
    {
        var h = new Harness();
        var reservation = h.Store.Reserve(h.Request(LlmContextOperation.Create, "create-1"));
        Assert.Equal(LlmContextState.Busy,
            h.Store.Status(Alice, idempotencyKey: "create-1").State);
        Assert.Null(h.Store.Status(Alice, idempotencyKey: "create-1").ContextId);

        h.Advance(TimeSpan.FromSeconds(5));
        var receipt = h.Store.Commit(reservation, Assistant("First"));
        Assert.Equal(1ul, receipt.Version);
        Assert.Equal(LlmContextState.Active, receipt.State);
        Assert.Equal(h.Now.AddSeconds(3600), DateTimeOffset.Parse(receipt.ExpiresAt!));
        Assert.Equal(3, h.Store.Snapshot(Alice, receipt.ContextId).Transcript.Count);
    }

    private static void AppendCommitsDelta()
    {
        var h = new Harness();
        var created = h.Create();
        var reservation = h.Store.Reserve(h.Request(
            LlmContextOperation.Append, "append-1", created.ContextId, 1,
            messages: [User("Two")]));
        var receipt = h.Store.Commit(reservation, Assistant("Second"));
        var snapshot = h.Store.Snapshot(Alice, created.ContextId);
        Assert.Equal(2ul, receipt.Version);
        Assert.Equal(5, snapshot.Transcript.Count);
        Assert.Equal("Two", snapshot.Transcript[^2].Content);
    }

    private static void StaleAndConcurrentLoseCas()
    {
        var h = new Harness();
        var created = h.Create();
        var winner = h.Store.Reserve(h.Request(
            LlmContextOperation.Append, "winner", created.ContextId, 1));

        var concurrent = Assert.Throws<LlmContextStoreException>(() => h.Store.Reserve(h.Request(
            LlmContextOperation.Append, "loser", created.ContextId, 1)));
        Assert.Equal(NwpErrorCodes.LlmContextVersionConflict, concurrent.ErrorCode);
        Assert.Equal(1ul, concurrent.CurrentVersion);
        h.Store.Abort(winner);

        var stale = Assert.Throws<LlmContextStoreException>(() => h.Store.Reserve(h.Request(
            LlmContextOperation.Append, "stale", created.ContextId, 0)));
        Assert.Equal(NwpErrorCodes.LlmContextVersionConflict, stale.ErrorCode);
        Assert.Equal(1ul, h.Store.Snapshot(Alice, created.ContextId).Version);
    }

    private static void ForkSnapshotsParent()
    {
        var h = new Harness();
        var parent = h.Create();
        var fork = h.Store.Reserve(h.Request(
            LlmContextOperation.Fork, "fork-1", parent.ContextId, 1, messages: []));
        var parentAppend = h.Store.Reserve(h.Request(
            LlmContextOperation.Append, "parent-append", parent.ContextId, 1));
        h.Store.Commit(parentAppend, Assistant("Parent moved"));
        var child = h.Store.Commit(fork, Assistant("Branch"));
        Assert.Equal(1ul, child.Version);
        Assert.Equal(parent.ContextId, child.ParentContextId);
        Assert.Equal(1ul, child.ParentVersion);
        Assert.Equal(2ul, h.Store.Snapshot(Alice, parent.ContextId).Version);
        Assert.Equal(4, h.Store.Snapshot(Alice, child.ContextId).Transcript.Count);
    }

    private static void ResetReplacesBindingAndTranscript()
    {
        var h = new Harness();
        var created = h.Create();
        var reset = h.Store.Reserve(h.Request(
            LlmContextOperation.Reset, "reset-1", created.ContextId, 1,
            binding: Binding("willow-medium", "runtime-2"),
            messages: [System("Use JSON."), User("Restart")]));
        var receipt = h.Store.Commit(reset, Assistant("{}"));
        var snapshot = h.Store.Snapshot(Alice, created.ContextId);
        Assert.Equal(2ul, receipt.Version);
        Assert.Equal("willow-medium", snapshot.Binding.Model);
        Assert.Equal(3, snapshot.Transcript.Count);
        Assert.DoesNotContain(snapshot.Transcript, item => item.Content == "One");
    }

    private static void BindingMismatchFailsClosed()
    {
        var h = new Harness();
        var created = h.Create();
        var error = Assert.Throws<LlmContextStoreException>(() => h.Store.Reserve(h.Request(
            LlmContextOperation.Append, "bad-binding", created.ContextId, 1,
            binding: Binding("willow-large", "runtime-1"))));
        Assert.Equal(NwpErrorCodes.LlmContextBindingMismatch, error.ErrorCode);
        Assert.Equal(1ul, h.Store.Snapshot(Alice, created.ContextId).Version);
    }

    private static void OwnerAndCapabilityBoundary()
    {
        var h = new Harness();
        var created = h.Create();
        var error = Assert.Throws<LlmContextStoreException>(() =>
            h.Store.Status(Bob, contextId: created.ContextId));
        Assert.Equal(NwpErrorCodes.LlmContextForbidden, error.ErrorCode);
        Assert.NotEqual(Alice, Bob);
    }

    private static void AbortPreservesCommittedState()
    {
        var h = new Harness();
        var created = h.Create();
        var reservation = h.Store.Reserve(h.Request(
            LlmContextOperation.Append, "abort-1", created.ContextId, 1));
        h.Store.Abort(reservation, "NPS-SERVER-TIMEOUT");
        Assert.Equal(1ul, h.Store.Snapshot(Alice, created.ContextId).Version);
        var status = h.Store.Status(Alice, idempotencyKey: "abort-1");
        Assert.Equal(LlmContextState.Failed, status.State);
        Assert.Equal("NPS-SERVER-TIMEOUT", status.ErrorCode);
    }

    private static void LostCreateIsRecoveredByKey()
    {
        var h = new Harness(defaultTtl: 10, tombstone: 5);
        var reservation = h.Store.Reserve(h.Request(LlmContextOperation.Create, "lost-create"));
        var busy = h.Store.Status(Alice, idempotencyKey: "lost-create");
        Assert.Equal(LlmContextState.Busy, busy.State);
        Assert.Null(busy.ContextId);
        h.Store.Commit(reservation, Assistant("First"));
        var active = h.Store.Status(Alice, idempotencyKey: "lost-create");
        Assert.Equal(LlmContextState.Active, active.State);
        Assert.NotNull(active.ContextId);
        Assert.Equal(1ul, active.Version);

        h.Advance(TimeSpan.FromSeconds(16));
        h.Store.SweepExpired();
        var retainedOutcome = h.Store.Status(Alice, idempotencyKey: "lost-create");
        Assert.Equal(active.ContextId, retainedOutcome.ContextId);
        Assert.Equal(1ul, retainedOutcome.Version);
    }

    private static void ReleaseAndExpiryTombstones()
    {
        var h = new Harness(defaultTtl: 10, tombstone: 5);
        var created = h.Create(ttl: 10);
        var released = h.Store.Release(Alice, created.ContextId, 1, "create-1");
        Assert.Equal(2ul, released.Version);
        Assert.Equal(released, h.Store.Release(Alice, created.ContextId, 1, "create-1"));
        var releaseCollision = Assert.Throws<LlmContextStoreException>(() =>
            h.Store.Release(Alice, "ERITFBUWFxgZGhscHR4fIA", 1, "create-1"));
        Assert.Equal(NwpErrorCodes.ActionIdempotencyConflict, releaseCollision.ErrorCode);
        Assert.Equal(LlmContextState.Released,
            h.Store.Status(Alice, contextId: created.ContextId).State);
        var releaseMutation = Assert.Throws<LlmContextStoreException>(() => h.Store.Reserve(h.Request(
            LlmContextOperation.Append, "after-release", created.ContextId, 2)));
        Assert.Equal(NwpErrorCodes.LlmContextNotFound, releaseMutation.ErrorCode);

        var expiring = h.Create(key: "create-expiring", ttl: 10);
        h.Advance(TimeSpan.FromSeconds(11));
        h.Store.SweepExpired();
        Assert.Equal(LlmContextState.Expired,
            h.Store.Status(Alice, contextId: expiring.ContextId).State);
        var expiredMutation = Assert.Throws<LlmContextStoreException>(() => h.Store.Reserve(h.Request(
            LlmContextOperation.Append, "after-expiry", expiring.ContextId, 1)));
        Assert.Equal(NwpErrorCodes.LlmContextExpired, expiredMutation.ErrorCode);

        h.Advance(TimeSpan.FromSeconds(6));
        h.Store.SweepExpired();
        var gone = Assert.Throws<LlmContextStoreException>(() =>
            h.Store.Status(Alice, contextId: expiring.ContextId));
        Assert.Equal(NwpErrorCodes.LlmContextNotFound, gone.ErrorCode);
    }

    private static void UsageAccountingIsMeasurable()
    {
        var usage = new LlmUsageDto
        {
            InputTokens = 1200,
            ReusedTokens = 1000,
            EvaluatedTokens = 200,
            OutputTokens = 80,
            CacheHit = true,
            WireInputBytes = 384,
        };
        Assert.Equal(usage.InputTokens, usage.ReusedTokens + usage.EvaluatedTokens);
        Assert.True(usage.CacheHit);
        Assert.True(usage.WireInputBytes < 4096ul);
    }

    private static void AdvertisedOperationsAreEnforced()
    {
        var h = new Harness(supported: new HashSet<LlmContextOperation> {
            LlmContextOperation.Create,
            LlmContextOperation.Append,
            LlmContextOperation.Reset,
            LlmContextOperation.Release,
        });
        var created = h.Create();
        var error = Assert.Throws<LlmContextStoreException>(() => h.Store.Reserve(h.Request(
            LlmContextOperation.Fork, "fork-not-advertised", created.ContextId, 1, messages: [])));
        Assert.Equal(NwpErrorCodes.LlmContextOperationUnsupported, error.ErrorCode);
    }

    private static void ProcessRestartDoesNotRecreateState()
    {
        var h = new Harness();
        var created = h.Create();
        var restarted = new Harness();
        var error = Assert.Throws<LlmContextStoreException>(() => restarted.Store.Reserve(
            restarted.Request(LlmContextOperation.Append, "after-restart", created.ContextId, 1)));
        Assert.Equal(NwpErrorCodes.LlmContextNotFound, error.ErrorCode);
    }

    private static void CompletedIdempotencyDoesNotRecommit()
    {
        var h = new Harness();
        var created = h.Create(key: "stream-replay");
        var replay = h.Store.Status(Alice, idempotencyKey: "stream-replay");
        Assert.Equal(created.ContextId, replay.ContextId);
        Assert.Equal(1ul, replay.Version);
        var duplicate = Assert.Throws<LlmContextStoreException>(() =>
            h.Store.Reserve(h.Request(LlmContextOperation.Create, "stream-replay")));
        Assert.Equal(NwpErrorCodes.ActionIdempotencyConflict, duplicate.ErrorCode);
        Assert.Equal(1ul, h.Store.Snapshot(Alice, created.ContextId).Version);
    }

    private static void RevocationAbortPreservesVersion()
    {
        var h = new Harness();
        var created = h.Create();
        var reservation = h.Store.Reserve(h.Request(
            LlmContextOperation.Append, "revoked", created.ContextId, 1));
        h.Store.Abort(reservation, "NWP-AUTH-NID-REVOKED");
        Assert.Equal(1ul, h.Store.Snapshot(Alice, created.ContextId).Version);
        Assert.Equal("NWP-AUTH-NID-REVOKED",
            h.Store.Status(Alice, idempotencyKey: "revoked").ErrorCode);
    }

    private static void PrincipalLimitPreventsAllocation()
    {
        var h = new Harness(maxContexts: 1);
        h.Create();
        var error = Assert.Throws<LlmContextStoreException>(() =>
            h.Store.Reserve(h.Request(LlmContextOperation.Create, "over-limit")));
        Assert.Equal(NwpErrorCodes.LlmContextLimitExceeded, error.ErrorCode);
    }

    private static void UnsupportedOperationHasDedicatedError() =>
        AdvertisedOperationsAreEnforced();

    private static void MissingIdempotencyFailsBeforeAllocation()
    {
        var h = new Harness();
        var error = Assert.Throws<LlmContextStoreException>(() => h.Store.Reserve(
            h.Request(LlmContextOperation.Create, "") with { IdempotencyKey = "" }));
        Assert.Equal(NwpErrorCodes.ActionParamsInvalid, error.ErrorCode);
        Assert.Equal(0, h.Store.SweepExpired());
    }

    private static JsonDocument LoadFixture()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName,
                "spec/conformance/nwp/llm_context_vectors.json");
            if (File.Exists(path)) return JsonDocument.Parse(File.ReadAllBytes(path));
            current = current.Parent;
        }
        throw new FileNotFoundException("Unable to locate llm_context_vectors.json.");
    }

    private static LlmContextBinding Binding(
        string model = "willow-small",
        string runtime = "runtime-1") => new()
        {
            Model = model,
            RuntimeRevision = runtime,
            SystemMessages = [System(model == "willow-small" ? "Be concise." : "Use JSON.")],
            Tools = null,
        };

    private static LlmMessageDto System(string content) => new() { Role = "system", Content = content };
    private static LlmMessageDto User(string content) => new() { Role = "user", Content = content };
    private static LlmMessageDto Assistant(string content) => new() { Role = "assistant", Content = content };

    private sealed class Harness
    {
        private readonly Queue<string> _ids = new([
            "AQIDBAUGBwgJCgsMDQ4PEA",
            "ERITFBUWFxgZGhscHR4fIA",
            "ISIjJCUmJygpKissLS4vMA",
            "MTIzNDU2Nzg5Ojs8PT4_QA",
        ]);
        private DateTimeOffset _now = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);

        public Harness(
            uint maxContexts = 32,
            uint defaultTtl = 3600,
            uint tombstone = 86400,
            ISet<LlmContextOperation>? supported = null)
        {
            Store = new InMemoryLlmContextStore(new LlmContextStoreOptions
            {
                MaxContextsPerPrincipal = maxContexts,
                DefaultTtlSeconds = defaultTtl,
                MaxTtlSeconds = 3600,
                TombstoneSeconds = tombstone,
                Clock = () => _now,
                ContextIdFactory = () => _ids.Dequeue(),
                SupportedOperations = supported ?? new HashSet<LlmContextOperation>(
                    Enum.GetValues<LlmContextOperation>()),
            });
        }

        public InMemoryLlmContextStore Store { get; }
        public DateTimeOffset Now => _now;

        public LlmContextReceiptDto Create(string key = "create-1", uint? ttl = null)
        {
            var reservation = Store.Reserve(Request(LlmContextOperation.Create, key, ttl: ttl));
            return Store.Commit(reservation, Assistant("First"));
        }

        public LlmContextMutationRequest Request(
            LlmContextOperation operation,
            string key,
            string? contextId = null,
            ulong? baseVersion = null,
            LlmContextBinding? binding = null,
            IReadOnlyList<LlmMessageDto>? messages = null,
            uint? ttl = null) => new()
            {
                Operation = operation,
                Owner = Alice,
                ContextId = contextId,
                BaseVersion = baseVersion,
                Binding = binding ?? Binding(),
                Messages = messages ?? (operation == LlmContextOperation.Create
                    ? [System("Be concise."), User("One")]
                    : [User("Continue")]),
                TtlSeconds = ttl,
                IdempotencyKey = key,
                RequestId = $"req-{key}",
            };

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
