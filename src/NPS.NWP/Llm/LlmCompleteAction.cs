// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Actions;
using NPS.NWP.Frames;

namespace NPS.NWP.Llm;

/// <summary>Official NWP action identifier for a non-streaming or streaming LLM completion request.</summary>
public static class LlmCompleteAction
{
    public const string ActionId = "llm.complete";
    public const string ResponseAnchorRef = "nps:system:llm.complete:response";
    public const string StreamAnchorRef = "nps:system:llm.complete:stream";

    public const string CapabilityComplete = "llm:complete";
    public const string CapabilityContext = "llm:context";
    public const string CapabilityStream = "llm:stream";
    public const string CapabilityToolCall = "llm:tool_call";
    public const string CapabilityEmbed = "llm:embed";
    public const string CapabilityRerank = "llm:rerank";

    public const string ReasoningVisibilityNone = "none";
    public const string ReasoningVisibilitySummary = "summary";
    public const string ReasoningVisibilityTrace = "trace";

    public static ActionFrame ToActionFrame(
        LlmCompleteActionRequest request,
        NwpActionFrameOptions? options = null)
    {
        var normalized = string.Equals(request.Kind, ActionId, StringComparison.Ordinal)
            ? request
            : request with { Kind = ActionId };

        return NwpActionPayloadCodec.ToActionFrame(ActionId, normalized, options);
    }

    public static LlmCompleteActionRequest ReadRequest(ActionFrame frame)
    {
        if (!TryReadRequest(frame, out var request, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return request!;
    }

    public static bool TryReadRequest(
        ActionFrame frame,
        out LlmCompleteActionRequest? request,
        out string? error)
    {
        if (!NwpActionPayloadCodec.TryReadPayload(frame, ActionId, out request, out error))
        {
            return false;
        }

        if (!string.Equals(request!.Kind, ActionId, StringComparison.Ordinal))
        {
            error = $"LLM action payload kind '{request.Kind}' does not match '{ActionId}'.";
            request = null;
            return false;
        }

        error = null;
        return true;
    }

    public static JsonElement ToResponsePayload(LlmCompleteActionResponse response) =>
        NwpActionPayloadCodec.ToJsonElement(response);

    public static LlmCompleteActionResponse ReadResponsePayload(JsonElement payload) =>
        NwpActionPayloadCodec.ReadJsonElement<LlmCompleteActionResponse>(payload);

    public static CapsFrame ToCapsFrame(
        LlmCompleteActionResponse response,
        uint? tokenEst = null,
        string? tokenizerUsed = null,
        string? requestId = null) =>
        NwpFramePayloadCodec.ToCapsFrame(
            ResponseAnchorRef,
            response,
            tokenEst,
            tokenizerUsed,
            requestId);

    public static LlmCompleteActionResponse ReadResponse(CapsFrame frame) =>
        NwpFramePayloadCodec.ReadCapsPayload<LlmCompleteActionResponse>(frame);

    public static StreamFrame ToStreamFrame(
        string streamId,
        uint seq,
        bool isLast,
        IEnumerable<LlmCompleteStreamChunkDto> chunks,
        bool includeAnchorRef = false,
        uint? windowSize = null,
        string? errorCode = null) =>
        NwpFramePayloadCodec.ToStreamFrame(
            streamId,
            seq,
            isLast,
            chunks,
            includeAnchorRef ? StreamAnchorRef : null,
            windowSize,
            errorCode);

    public static IReadOnlyList<LlmCompleteStreamChunkDto> ReadStreamChunks(StreamFrame frame) =>
        NwpFramePayloadCodec.ReadStreamPayloads<LlmCompleteStreamChunkDto>(frame);

    public static LlmCompleteActionResponse ReadAsyncResult(ActionTaskStatus status) =>
        NwpFramePayloadCodec.ReadTaskResult<LlmCompleteActionResponse>(status);

    public static bool TryReadAsyncResult(
        ActionTaskStatus status,
        out LlmCompleteActionResponse? response,
        out string? error) =>
        NwpFramePayloadCodec.TryReadTaskResult(status, out response, out error);
}

public sealed record LlmCompleteActionRequest
{
    public string Kind { get; init; } = LlmCompleteAction.ActionId;

    public required string Model { get; init; }

    [JsonPropertyName("max_tokens")]
    public uint? MaxTokens { get; init; }

    public bool Stream { get; init; }

    public required IReadOnlyList<LlmMessageDto> Messages { get; init; }

    public IReadOnlyList<LlmToolDefinitionDto>? Tools { get; init; }

    public LlmContextRequestDto? Context { get; init; }
}

public sealed record LlmCompleteActionResponse
{
    [JsonPropertyName("stop_reason")]
    public required LlmStopReason StopReason { get; init; }

    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<LlmToolCallDto>? ToolCalls { get; init; }

    /// <summary>
    /// Model/provider-level completion error. Protocol errors SHOULD be returned
    /// as ErrorFrame instead of this field.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Actual model/provider usage. Estimates belong on <c>CapsFrame.token_est</c>.</summary>
    public LlmUsageDto? Usage { get; init; }

    public LlmContextReceiptDto? Context { get; init; }
}

/// <summary>
/// Streaming chunk carried by <c>StreamFrame.Data[]</c> when
/// <see cref="LlmCompleteActionRequest.Stream"/> is true.
/// </summary>
public sealed record LlmCompleteStreamChunkDto
{
    [JsonPropertyName("content_delta")]
    public string? ContentDelta { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<LlmToolCallDto>? ToolCalls { get; init; }

    [JsonPropertyName("stop_reason")]
    public LlmStopReason? StopReason { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// Actual model/provider usage. Producers SHOULD emit this only on the terminal chunk.
    /// </summary>
    public LlmUsageDto? Usage { get; init; }

    /// <summary>Committed context receipt. Valid only on a successful terminal chunk.</summary>
    public LlmContextReceiptDto? Context { get; init; }
}

/// <summary>
/// Actual token and prefix/KV-cache usage reported by the model runtime. Every
/// field is optional because providers expose different levels of accounting.
/// </summary>
public sealed record LlmUsageDto
{
    /// <summary>Total logical model-input tokens, including any reused prefix.</summary>
    [JsonPropertyName("input_tokens")]
    public uint? InputTokens { get; init; }

    /// <summary>Tokens generated by the model for this completion.</summary>
    [JsonPropertyName("output_tokens")]
    public uint? OutputTokens { get; init; }

    /// <summary>Whether the model runtime reused a prefix/KV cache entry.</summary>
    [JsonPropertyName("cache_hit")]
    public bool? CacheHit { get; init; }

    /// <summary>Input tokens reused from prefix/KV cache without new evaluation.</summary>
    [JsonPropertyName("reused_tokens")]
    public uint? ReusedTokens { get; init; }

    /// <summary>Input tokens newly evaluated by the model for this invocation.</summary>
    [JsonPropertyName("evaluated_tokens")]
    public uint? EvaluatedTokens { get; init; }

    /// <summary>Decoder-observed serialized ActionFrame payload bytes.</summary>
    [JsonPropertyName("wire_input_bytes")]
    public ulong? WireInputBytes { get; init; }
}

public enum LlmContextOperation
{
    Create,
    Append,
    Fork,
    Reset,
    Release,
}

public enum LlmContextState
{
    Busy,
    Active,
    Released,
    Expired,
    Failed,
}

public sealed record LlmContextRequestDto
{
    public required LlmContextOperation Operation { get; init; }

    [JsonPropertyName("context_id")]
    public string? ContextId { get; init; }

    [JsonPropertyName("base_version")]
    public ulong? BaseVersion { get; init; }

    [JsonPropertyName("ttl_seconds")]
    public uint? TtlSeconds { get; init; }
}

public sealed record LlmContextReceiptDto
{
    [JsonPropertyName("context_id")]
    public required string ContextId { get; init; }

    public required ulong Version { get; init; }

    public required LlmContextOperation Operation { get; init; }

    public required LlmContextState State { get; init; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; init; }

    [JsonPropertyName("parent_context_id")]
    public string? ParentContextId { get; init; }

    [JsonPropertyName("parent_version")]
    public ulong? ParentVersion { get; init; }
}

public sealed record LlmContextStatusRequestDto
{
    [JsonPropertyName("context_id")]
    public string? ContextId { get; init; }

    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }
}

public sealed record LlmContextReleaseRequestDto
{
    [JsonPropertyName("context_id")]
    public required string ContextId { get; init; }

    [JsonPropertyName("base_version")]
    public required ulong BaseVersion { get; init; }
}

public sealed record LlmContextStatusDto
{
    public required LlmContextState State { get; init; }

    [JsonPropertyName("context_id")]
    public string? ContextId { get; init; }

    public ulong? Version { get; init; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }
}

public static class LlmContextActions
{
    public const string StatusActionId = "llm.context.status";
    public const string ReleaseActionId = "llm.context.release";
    public const string StatusResponseAnchorRef = "nps:system:llm.context.status:response";
    public const string ReleaseResponseAnchorRef = "nps:system:llm.context.release:response";

    public static ActionFrame ToStatusActionFrame(
        LlmContextStatusRequestDto request,
        NwpActionFrameOptions? options = null) =>
        NwpActionPayloadCodec.ToActionFrame(StatusActionId, request, options);

    public static ActionFrame ToReleaseActionFrame(
        LlmContextReleaseRequestDto request,
        NwpActionFrameOptions options) =>
        NwpActionPayloadCodec.ToActionFrame(ReleaseActionId, request, options);

    public static LlmContextStatusRequestDto ReadStatusRequest(ActionFrame frame) =>
        ReadPayload<LlmContextStatusRequestDto>(frame, StatusActionId);

    public static LlmContextReleaseRequestDto ReadReleaseRequest(ActionFrame frame) =>
        ReadPayload<LlmContextReleaseRequestDto>(frame, ReleaseActionId);

    private static T ReadPayload<T>(ActionFrame frame, string actionId)
    {
        if (!NwpActionPayloadCodec.TryReadPayload(frame, actionId, out T? payload, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return payload!;
    }
}

public sealed record LlmMessageDto
{
    public required string Role { get; init; }

    public string? Content { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<LlmToolCallDto>? ToolCalls { get; init; }
}

public sealed record LlmToolCallDto
{
    [JsonPropertyName("call_id")]
    public required string CallId { get; init; }

    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("arguments_json")]
    public required string ArgumentsJson { get; init; }
}

public sealed record LlmToolDefinitionDto
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<ToolParameterDto>? Parameters { get; init; }
}

public sealed record ToolParameterDto
{
    public required string Name { get; init; }

    public required string Type { get; init; }

    public string? Description { get; init; }

    public bool Required { get; init; }
}

public enum LlmStopReason
{
    EndTurn,
    ToolUse,
    ToolCalls,
    MaxTokens,
    Length,
    Error
}
