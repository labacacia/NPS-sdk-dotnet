// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core;
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
    public bool SupportsJsonMode { get; set; }
    public string? ReasoningVisibility { get; set; }
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
            SupportsStream = false,
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

        var owner = Owner(context);
        await CheckAuthorization(owner, frame.ActionId, LlmAuthorizationStage.Admission, context, ct);
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
        if (request.Stream)
            throw ActionNodeException.ParamsInvalid(
                "The Action Server context coordinator supports unary/async completion, not streaming.");
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
            await CheckAuthorization(owner, frame.ActionId, LlmAuthorizationStage.Commit, context, ct);
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
        ActionContext context,
        CancellationToken ct) =>
        _options.Authorizer?.Invoke(owner, actionId, stage, context, ct) ?? ValueTask.CompletedTask;

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
