// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NPS.Core;
using NPS.Core.Frames.Ncp;
using NPS.NWP.ActionNode;
using NPS.NWP.Actions;
using NPS.NWP.Frames;
using NPS.NWP.Http;
using NPS.NWP.Nwm;

namespace NPS.NWP.Llm;

public enum LlmAuthorizationStage
{
    Admission,
    Commit,
}

public delegate ValueTask LlmContextAuthorizer(
    LlmContextOwner owner,
    string actionId,
    LlmAuthorizationStage stage,
    IReadOnlyList<string> requiredCapabilities,
    ActionContext context,
    CancellationToken ct);

public sealed class StatefulLlmActionOptions
{
    public StatefulLlmActionOptions(string securityScope, string runtimeRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRevision);
        SecurityScope = securityScope;
        RuntimeRevision = runtimeRevision;
    }

    /// <summary>Deployment-authenticated tenant/workspace scope; never read from payloads.</summary>
    public string SecurityScope { get; }

    /// <summary>Provider/runtime compatibility revision included in immutable bindings.</summary>
    public string RuntimeRevision { get; }

    public string? ProviderName { get; set; }
    public string? DefaultModel { get; set; }
    public bool SupportsTools { get; set; }
    public bool SupportsStream { get; set; }
    public bool SupportsJsonMode { get; set; }
    public string? ReasoningVisibility { get; set; }
    /// <summary>
    /// Deployment-owned NIP verifier. Stateful requests fail closed when absent;
    /// the callback must verify every supplied capability at both stages.
    /// </summary>
    public LlmContextAuthorizer? Authorizer { get; set; }
}

/// <summary>
/// Wraps an ordinary LLM Action Node provider with the official NWP 0.21
/// stateful context lifecycle and its two-phase authorization contract.
/// </summary>
public sealed class StatefulLlmActionProvider : IActionNodeProvider
{
    public const string CompleteRequestAnchorRef = "nps:system:llm.complete:request";
    public const string StatusRequestAnchorRef = "nps:system:llm.context.status:request";
    public const string ReleaseRequestAnchorRef = "nps:system:llm.context.release:request";

    private readonly IActionNodeProvider _inner;
    private readonly InMemoryLlmContextStore _store;
    private readonly StatefulLlmActionOptions _options;

    public StatefulLlmActionProvider(
        IActionNodeProvider inner,
        InMemoryLlmContextStore store,
        StatefulLlmActionOptions options)
    {
        _inner = inner;
        _store = store;
        _options = options;
    }

    public InMemoryLlmContextStore Store => _store;

    /// <summary>Registers the exact actions and process-persistence profile implemented here.</summary>
    public void ConfigureNode(ActionNodeOptions node)
    {
        var actions = new Dictionary<string, ActionSpec>(node.Actions, StringComparer.Ordinal);
        actions.TryGetValue(LlmCompleteAction.ActionId, out var complete);
        actions[LlmCompleteAction.ActionId] = new ActionSpec
        {
            Description = complete?.Description ?? "Complete an LLM request",
            ParamsAnchor = CompleteRequestAnchorRef,
            ResultAnchor = LlmCompleteAction.ResponseAnchorRef,
            Async = complete?.Async ?? true,
            Idempotent = true,
            TimeoutMsDefault = complete?.TimeoutMsDefault,
            TimeoutMsMax = complete?.TimeoutMsMax,
            RequiredCapability = LlmCompleteAction.CapabilityComplete,
        };
        actions[LlmContextActions.StatusActionId] = new ActionSpec
        {
            Description = "Inspect an LLM context or retained create outcome",
            ParamsAnchor = StatusRequestAnchorRef,
            ResultAnchor = LlmContextActions.StatusResponseAnchorRef,
            Async = false,
            RequiredCapability = LlmCompleteAction.CapabilityContext,
        };
        actions[LlmContextActions.ReleaseActionId] = new ActionSpec
        {
            Description = "Release an LLM context",
            ParamsAnchor = ReleaseRequestAnchorRef,
            ResultAnchor = LlmContextActions.ReleaseResponseAnchorRef,
            Async = false,
            Idempotent = true,
            RequiredCapability = LlmCompleteAction.CapabilityContext,
        };
        node.Actions = actions;

        var descriptor = _store.Descriptor;
        node.LlmProfile = new NwmLlmProfile
        {
            ProfileVersion = "0.2",
            Actions =
            [
                LlmCompleteAction.ActionId,
                LlmContextActions.StatusActionId,
                LlmContextActions.ReleaseActionId,
            ],
            Provider = _options.ProviderName,
            DefaultModel = _options.DefaultModel,
            SupportsStream = _options.SupportsStream,
            SupportsTools = _options.SupportsTools,
            SupportsJsonMode = _options.SupportsJsonMode,
            ReasoningVisibility = _options.ReasoningVisibility,
            Context = new NwmLlmContextProfile
            {
                Supported = true,
                Operations = descriptor.Operations.Select(ToWireName).ToArray(),
                Persistence = descriptor.Persistence,
                MaxContextsPerPrincipal = descriptor.MaxContextsPerPrincipal,
                MaxTtlSeconds = descriptor.MaxTtlSeconds,
                TombstoneSeconds = descriptor.TombstoneSeconds,
            },
        };
    }

    public async Task AuthorizeAsync(
        ActionFrame frame,
        ActionContext context,
        CancellationToken ct = default)
    {
        var requiresContextAuthorization = frame.ActionId switch
        {
            LlmContextActions.StatusActionId or LlmContextActions.ReleaseActionId => true,
            LlmCompleteAction.ActionId => HasContextRequest(frame.Params),
            _ => false,
        };
        if (!requiresContextAuthorization) return;

        if (frame.ActionId == LlmCompleteAction.ActionId && frame.Async)
        {
            LlmCompleteActionRequest request;
            try
            {
                request = LlmCompleteAction.ReadRequest(frame);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                throw ActionNodeException.ParamsInvalid(ex.Message);
            }
            if (request.Stream)
                throw ActionNodeException.ParamsInvalid(
                    "stream=true cannot be combined with async=true.");
        }

        var owner = Owner(context);
        await CheckAuthorization(
            owner,
            frame.ActionId,
            LlmAuthorizationStage.Admission,
            RequiredCapabilities(frame),
            context,
            ct);
    }

    public Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame,
        ActionContext context,
        CancellationToken ct = default) => frame.ActionId switch
        {
            LlmCompleteAction.ActionId => Complete(frame, context, ct),
            LlmContextActions.StatusActionId => Task.FromResult(Status(frame, context)),
            LlmContextActions.ReleaseActionId => Task.FromResult(Release(frame, context)),
            _ => _inner.ExecuteAsync(frame, context, ct),
        };

    private async Task<ActionExecutionResult> Complete(
        ActionFrame frame,
        ActionContext context,
        CancellationToken ct)
    {
        LlmCompleteActionRequest request;
        try
        {
            request = LlmCompleteAction.ReadRequest(frame);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            throw ActionNodeException.ParamsInvalid(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(request.Model))
            throw ActionNodeException.ParamsInvalid("llm.complete requires a non-empty model.");
        if (!_options.SupportsTools && request.Tools is { Count: > 0 })
            throw ActionNodeException.ParamsInvalid("This node does not advertise LLM tool-definition support.");
        if (request.Context is null)
            return await _inner.ExecuteAsync(frame, context, ct);
        if (request.Stream && !_options.SupportsStream)
            throw ActionNodeException.ParamsInvalid(
                "This node does not advertise LLM streaming support.");
        if (request.Stream && frame.Async)
            throw ActionNodeException.ParamsInvalid(
                "stream=true cannot be combined with async=true.");
        if (request.Context.Operation is LlmContextOperation.Append
                or LlmContextOperation.Fork
                or LlmContextOperation.Reset
            && (string.IsNullOrWhiteSpace(request.Context.ContextId)
                || request.Context.BaseVersion is null))
            throw ActionNodeException.ParamsInvalid(
                "append/fork/reset require context_id and base_version.");

        var owner = Owner(context);
        var binding = ResolveBinding(owner, request);
        var mutation = new LlmContextMutationRequest
        {
            Operation = request.Context.Operation,
            Owner = owner,
            ContextId = request.Context.ContextId,
            BaseVersion = request.Context.BaseVersion,
            Binding = binding,
            Messages = request.Messages,
            TtlSeconds = request.Context.TtlSeconds,
            IdempotencyKey = frame.IdempotencyKey ?? string.Empty,
            RequestId = frame.RequestId ?? string.Empty,
        };

        LlmContextMutationReservation reservation;
        try
        {
            reservation = _store.Reserve(mutation);
        }
        catch (LlmContextStoreException ex)
        {
            throw MapStoreError(ex);
        }

        ActionExecutionResult result;
        try
        {
            result = await _inner.ExecuteAsync(frame, context, ct);
        }
        catch (OperationCanceledException)
        {
            Abort(reservation, NwpErrorCodes.NodeUnavailable);
            throw;
        }
        catch (ActionNodeException ex)
        {
            Abort(reservation, ex.ErrorCode);
            throw;
        }
        catch
        {
            Abort(reservation, NwpErrorCodes.NodeUnavailable);
            throw;
        }

        if (request.Stream)
        {
            if (result.StreamFrames is null)
            {
                Abort(reservation, NwpErrorCodes.NodeUnavailable);
                throw ActionNodeException.Internal(
                    "Stateful streaming llm.complete returned no StreamFrame sequence.");
            }
            return new ActionExecutionResult
            {
                StreamFrames = CoordinateStream(
                    result.StreamFrames, reservation, owner, frame, context, ct),
                AnchorRef = result.AnchorRef ?? LlmCompleteAction.StreamAnchorRef,
                TokenEst = result.TokenEst,
            };
        }

        if (ct.IsCancellationRequested)
        {
            Abort(reservation, NwpErrorCodes.NodeUnavailable);
            ct.ThrowIfCancellationRequested();
        }

        if (result.Result is null)
        {
            Abort(reservation, NwpErrorCodes.NodeUnavailable);
            throw ActionNodeException.Internal("Stateful llm.complete returned no result payload.");
        }

        LlmCompleteActionResponse response;
        try
        {
            response = LlmCompleteAction.ReadResponsePayload(result.Result.Value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            Abort(reservation, NwpErrorCodes.NodeUnavailable);
            throw ActionNodeException.Internal($"Stateful llm.complete returned an invalid official response: {ex.Message}");
        }

        if (response.StopReason == LlmStopReason.Error)
        {
            Abort(reservation, NwpErrorCodes.NodeUnavailable);
            response = response with { Context = null };
            return Result(response, result);
        }

        try
        {
            await CheckAuthorization(
                owner,
                frame.ActionId,
                LlmAuthorizationStage.Commit,
                RequiredCapabilities(frame),
                context,
                ct);
        }
        catch (ActionNodeException ex)
        {
            Abort(reservation, ex.ErrorCode);
            throw;
        }
        catch
        {
            Abort(reservation, NwpErrorCodes.NodeUnavailable);
            throw;
        }

        var assistant = new LlmMessageDto
        {
            Role = "assistant",
            Content = response.Content,
            ToolCalls = response.ToolCalls,
        };
        LlmContextReceiptDto receipt;
        try
        {
            receipt = _store.Commit(reservation, assistant);
        }
        catch (LlmContextStoreException ex)
        {
            throw MapStoreError(ex);
        }
        response = response with { Context = receipt };
        return Result(response, result);
    }

    private async IAsyncEnumerable<StreamFrame> CoordinateStream(
        IAsyncEnumerable<StreamFrame> source,
        LlmContextMutationReservation reservation,
        LlmContextOwner owner,
        ActionFrame requestFrame,
        ActionContext actionContext,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var content = new StringBuilder();
        var toolCalls = new List<LlmToolCallDto>();
        var resolved = false;

        try
        {
            await foreach (var frame in source.WithCancellation(ct))
            {
                IReadOnlyList<LlmCompleteStreamChunkDto> chunks;
                try
                {
                    chunks = LlmCompleteAction.ReadStreamChunks(frame);
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException)
                {
                    throw ActionNodeException.Internal(
                        $"Stateful llm.complete returned an invalid stream payload: {ex.Message}");
                }

                if (!frame.IsLast && chunks.Any(chunk =>
                        chunk.StopReason is not null ||
                        chunk.Error is not null ||
                        chunk.Usage is not null ||
                        chunk.Context is not null))
                {
                    throw ActionNodeException.Internal(
                        "LLM stream stop_reason, error, usage, and context are terminal-only fields.");
                }

                foreach (var chunk in chunks)
                {
                    if (chunk.ContentDelta is not null)
                        content.Append(chunk.ContentDelta);
                    if (chunk.ToolCalls is not null)
                        toolCalls.AddRange(chunk.ToolCalls);
                }

                var sanitized = chunks.Select(chunk => chunk with { Context = null }).ToArray();
                if (!frame.IsLast)
                {
                    yield return RewriteStreamPayload(frame, sanitized);
                    continue;
                }

                var terminal = sanitized.LastOrDefault(chunk => chunk.StopReason is not null);
                var failed = frame.ErrorCode is not null ||
                    sanitized.Any(chunk =>
                        chunk.StopReason == LlmStopReason.Error || chunk.Error is not null);
                if (failed)
                {
                    Abort(reservation, frame.ErrorCode ?? NwpErrorCodes.NodeUnavailable);
                    resolved = true;
                    yield return RewriteStreamPayload(
                        frame with
                        {
                            IsLast = true,
                            ErrorCode = frame.ErrorCode ?? NwpErrorCodes.NodeUnavailable,
                        },
                        sanitized);
                    yield break;
                }
                if (terminal?.StopReason is null)
                {
                    throw ActionNodeException.Internal(
                        "Successful LLM stream terminal frame requires stop_reason.");
                }

                try
                {
                    await CheckAuthorization(
                        owner,
                        requestFrame.ActionId,
                        LlmAuthorizationStage.Commit,
                        RequiredCapabilities(requestFrame),
                        actionContext,
                        ct);
                }
                catch (ActionNodeException ex)
                {
                    Abort(reservation, ex.ErrorCode);
                    resolved = true;
                    throw;
                }

                LlmContextReceiptDto receipt;
                try
                {
                    receipt = _store.Commit(reservation, new LlmMessageDto
                    {
                        Role = "assistant",
                        Content = content.Length == 0 ? null : content.ToString(),
                        ToolCalls = toolCalls.Count == 0 ? null : toolCalls,
                    });
                }
                catch (LlmContextStoreException ex)
                {
                    Abort(reservation, ex.ErrorCode);
                    resolved = true;
                    throw MapStoreError(ex);
                }
                resolved = true;
                var committed = sanitized
                    .Select(chunk => ReferenceEquals(chunk, terminal)
                        ? chunk with { Context = receipt }
                        : chunk)
                    .ToArray();
                yield return RewriteStreamPayload(frame, committed);
                yield break;
            }

            throw ActionNodeException.Internal(
                "Stateful llm.complete stream ended without a terminal frame.");
        }
        finally
        {
            if (!resolved)
                Abort(reservation, NwpErrorCodes.NodeUnavailable);
        }
    }

    private static StreamFrame RewriteStreamPayload(
        StreamFrame frame,
        IReadOnlyList<LlmCompleteStreamChunkDto> chunks) => frame with
        {
            Data = chunks.Select(NwpActionPayloadCodec.ToJsonElement).ToArray(),
        };

    private ActionExecutionResult Status(ActionFrame frame, ActionContext context)
    {
        LlmContextStatusRequestDto request;
        try
        {
            request = LlmContextActions.ReadStatusRequest(frame);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            throw ActionNodeException.ParamsInvalid(ex.Message);
        }
        try
        {
            var status = _store.Status(Owner(context), request.ContextId, request.IdempotencyKey);
            return new ActionExecutionResult
            {
                Result = NwpActionPayloadCodec.ToJsonElement(status),
                AnchorRef = LlmContextActions.StatusResponseAnchorRef,
            };
        }
        catch (LlmContextStoreException ex)
        {
            throw MapStoreError(ex);
        }
    }

    private ActionExecutionResult Release(ActionFrame frame, ActionContext context)
    {
        LlmContextReleaseRequestDto request;
        try
        {
            request = LlmContextActions.ReadReleaseRequest(frame);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            throw ActionNodeException.ParamsInvalid(ex.Message);
        }
        try
        {
            var receipt = _store.Release(
                Owner(context),
                request.ContextId,
                request.BaseVersion,
                frame.IdempotencyKey ?? string.Empty);
            return new ActionExecutionResult
            {
                Result = NwpActionPayloadCodec.ToJsonElement(receipt),
                AnchorRef = LlmContextActions.ReleaseResponseAnchorRef,
            };
        }
        catch (LlmContextStoreException ex)
        {
            throw MapStoreError(ex);
        }
    }

    private LlmContextBinding ResolveBinding(
        LlmContextOwner owner,
        LlmCompleteActionRequest request)
    {
        if (request.Context!.Operation is LlmContextOperation.Append or LlmContextOperation.Fork)
        {
            if (request.Context.ContextId is null)
                throw ActionNodeException.ParamsInvalid("append/fork require context_id and base_version.");
            try
            {
                var snapshot = _store.Snapshot(owner, request.Context.ContextId);
                return new LlmContextBinding
                {
                    Model = request.Model,
                    SystemMessages = snapshot.Binding.SystemMessages,
                    Tools = request.Tools ?? snapshot.Binding.Tools,
                    RuntimeRevision = _options.RuntimeRevision,
                };
            }
            catch (LlmContextStoreException ex)
            {
                throw MapStoreError(ex);
            }
        }

        return new LlmContextBinding
        {
            Model = request.Model,
            SystemMessages = request.Messages
                .Where(item => string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            Tools = request.Tools,
            RuntimeRevision = _options.RuntimeRevision,
        };
    }

    private LlmContextOwner Owner(ActionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.AgentNid))
        {
            throw new ActionNodeException(
                401,
                NpsStatusCodes.AuthUnauthenticated,
                NwpErrorCodes.AuthNidScopeViolation,
                "Stateful LLM context actions require an authenticated agent NID.");
        }
        return new LlmContextOwner(context.AgentNid, _options.SecurityScope);
    }

    private ValueTask CheckAuthorization(
        LlmContextOwner owner,
        string actionId,
        LlmAuthorizationStage stage,
        IReadOnlyList<string> requiredCapabilities,
        ActionContext context,
        CancellationToken ct)
    {
        if (_options.Authorizer is null)
        {
            throw new ActionNodeException(
                403,
                NpsStatusCodes.AuthForbidden,
                NwpErrorCodes.LlmContextForbidden,
                "Stateful LLM context authorization is not configured.");
        }
        return _options.Authorizer.Invoke(
            owner, actionId, stage, requiredCapabilities, context, ct);
    }

    private static IReadOnlyList<string> RequiredCapabilities(ActionFrame frame)
    {
        if (frame.ActionId is LlmContextActions.StatusActionId or LlmContextActions.ReleaseActionId)
            return [LlmCompleteAction.CapabilityContext];

        var capabilities = new List<string>
        {
            LlmCompleteAction.CapabilityComplete,
            LlmCompleteAction.CapabilityContext,
        };
        if (frame.Params is { ValueKind: JsonValueKind.Object } parameters)
        {
            if (parameters.TryGetProperty("stream", out var stream)
                && stream.ValueKind is JsonValueKind.True)
                capabilities.Add(LlmCompleteAction.CapabilityStream);
            if (parameters.TryGetProperty("tools", out var tools)
                && tools.ValueKind is JsonValueKind.Array
                && tools.GetArrayLength() > 0)
                capabilities.Add(LlmCompleteAction.CapabilityToolCall);
        }
        return capabilities;
    }

    private void Abort(LlmContextMutationReservation reservation, string errorCode)
    {
        try
        {
            _store.Abort(reservation, errorCode);
        }
        catch (InvalidOperationException ex)
        {
            throw ActionNodeException.Internal($"Failed to abort LLM context reservation: {ex.Message}");
        }
    }

    private static ActionExecutionResult Result(
        LlmCompleteActionResponse response,
        ActionExecutionResult providerResult) => new()
        {
            Result = LlmCompleteAction.ToResponsePayload(response),
            AnchorRef = providerResult.AnchorRef ?? LlmCompleteAction.ResponseAnchorRef,
            TokenEst = providerResult.TokenEst,
        };

    private static bool HasContextRequest(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } root &&
        root.TryGetProperty("context", out var context) &&
        context.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static string ToWireName(LlmContextOperation operation) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(operation.ToString());

    private static ActionNodeException MapStoreError(LlmContextStoreException ex) =>
        ex.ErrorCode switch
        {
            NwpErrorCodes.ActionParamsInvalid =>
                new(422, NpsStatusCodes.ClientUnprocessable, ex.ErrorCode, ex.Message),
            NwpErrorCodes.LlmContextNotFound =>
                new(404, NpsStatusCodes.ClientNotFound, ex.ErrorCode, ex.Message),
            NwpErrorCodes.LlmContextExpired =>
                new(410, NpsStatusCodes.ClientGone, ex.ErrorCode, ex.Message),
            NwpErrorCodes.LlmContextForbidden =>
                new(403, NpsStatusCodes.AuthForbidden, ex.ErrorCode, ex.Message),
            NwpErrorCodes.LlmContextLimitExceeded =>
                new(429, NpsStatusCodes.LimitResource, ex.ErrorCode, ex.Message),
            NwpErrorCodes.LlmContextOperationUnsupported =>
                new(501, NpsStatusCodes.ServerUnsupported, ex.ErrorCode, ex.Message),
            _ => new(409, NpsStatusCodes.ClientConflict, ex.ErrorCode, ex.Message),
        };
}
