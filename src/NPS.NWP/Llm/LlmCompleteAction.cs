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
        string? tokenizerUsed = null) =>
        NwpFramePayloadCodec.ToCapsFrame(
            ResponseAnchorRef,
            response,
            tokenEst,
            tokenizerUsed);

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
