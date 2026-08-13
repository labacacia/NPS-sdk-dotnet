// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Actions;

/// <summary>
/// Optional metadata copied onto an <see cref="ActionFrame"/> when a typed
/// payload DTO is mapped to wire form.
/// </summary>
public sealed record NwpActionFrameOptions
{
    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }

    [JsonPropertyName("timeout_ms")]
    public uint? TimeoutMs { get; init; }

    [JsonPropertyName("async")]
    public bool Async { get; init; }

    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; init; }

    public string? Priority { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }
}

/// <summary>
/// Typed representation of an NWP Action RPC request. The wire form remains
/// <see cref="ActionFrame"/>: <see cref="ActionId"/> maps to
/// <see cref="ActionFrame.ActionId"/> and <see cref="Payload"/> maps to
/// <see cref="ActionFrame.Params"/>.
/// </summary>
public sealed record NwpActionRequest<TPayload>
{
    [JsonPropertyName("action_id")]
    public required string ActionId { get; init; }

    public required TPayload Payload { get; init; }

    public NwpActionFrameOptions Options { get; init; } = new();

    public ActionFrame ToActionFrame() =>
        NwpActionPayloadCodec.ToActionFrame(ActionId, Payload, Options);

    public static NwpActionRequest<TPayload> FromActionFrame(ActionFrame frame) =>
        new()
        {
            ActionId = frame.ActionId,
            Payload = NwpActionPayloadCodec.ReadPayload<TPayload>(frame),
            Options = new NwpActionFrameOptions
            {
                IdempotencyKey = frame.IdempotencyKey,
                TimeoutMs = frame.TimeoutMs,
                Async = frame.Async,
                CallbackUrl = frame.CallbackUrl,
                Priority = frame.Priority,
                RequestId = frame.RequestId,
            },
        };
}

/// <summary>
/// Typed Action RPC success payload envelope for SDK consumers that want a
/// generic response shape. NWP's default synchronous transport response is a
/// <c>CapsFrame</c> whose first data item is the action-specific payload.
/// </summary>
public sealed record NwpActionResponse<TPayload>
{
    [JsonPropertyName("action_id")]
    public required string ActionId { get; init; }

    public required TPayload Result { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }
}

/// <summary>
/// Helpers for encoding typed ActionFrame payload DTOs. JSON uses NPS standard
/// snake_case field names and case-insensitive reads; MessagePack is derived
/// from the same JSON-compatible map so keys stay snake_case across tiers.
/// </summary>
public static class NwpActionPayloadCodec
{
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static ActionFrame ToActionFrame<TPayload>(
        string actionId,
        TPayload payload,
        NwpActionFrameOptions? options = null)
    {
        options ??= new NwpActionFrameOptions();

        return new ActionFrame
        {
            ActionId = actionId,
            Params = ToJsonElement(payload),
            IdempotencyKey = options.IdempotencyKey,
            TimeoutMs = options.TimeoutMs ?? 5000,
            Async = options.Async,
            CallbackUrl = options.CallbackUrl,
            Priority = options.Priority,
            RequestId = options.RequestId,
        };
    }

    public static JsonElement ToJsonElement<TPayload>(TPayload payload) =>
        JsonSerializer.SerializeToElement(payload, JsonOptions).Clone();

    public static TPayload ReadJsonElement<TPayload>(JsonElement payload)
    {
        var normalized = NormalizePropertyNames(payload);
        var decoded = normalized.Deserialize<TPayload>(JsonOptions);
        return decoded ?? throw new InvalidOperationException(
            $"JSON payload decoded to null for {typeof(TPayload).Name}.");
    }

    public static bool TryReadJsonElement<TPayload>(
        JsonElement? payloadElement,
        string payloadName,
        out TPayload? payload,
        out string? error)
    {
        payload = default;

        if (!payloadElement.HasValue ||
            payloadElement.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            error = $"{payloadName} does not contain a payload.";
            return false;
        }

        try
        {
            payload = ReadJsonElement<TPayload>(payloadElement.Value);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or NotSupportedException)
        {
            error = $"{payloadName} could not be decoded as {typeof(TPayload).Name}: {ex.Message}";
            return false;
        }
    }

    public static TPayload ReadPayload<TPayload>(ActionFrame frame)
    {
        if (!TryReadPayload(frame, out TPayload? payload, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return payload!;
    }

    public static bool TryReadPayload<TPayload>(
        ActionFrame frame,
        out TPayload? payload,
        out string? error)
    {
        return TryReadPayload(frame, expectedActionId: null, out payload, out error);
    }

    public static bool TryReadPayload<TPayload>(
        ActionFrame frame,
        string? expectedActionId,
        out TPayload? payload,
        out string? error)
    {
        payload = default;

        if (expectedActionId is not null &&
            !string.Equals(frame.ActionId, expectedActionId, StringComparison.Ordinal))
        {
            error = $"Unexpected action_id '{frame.ActionId}', expected '{expectedActionId}'.";
            return false;
        }

        return TryReadJsonElement(
            frame.Params,
            $"ActionFrame '{frame.ActionId}' params",
            out payload,
            out error);
    }

    public static byte[] EncodeJson<TPayload>(TPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

    public static TPayload DecodeJson<TPayload>(ReadOnlySpan<byte> payload)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        return ReadJsonElement<TPayload>(document.RootElement);
    }

    public static byte[] EncodeMsgPack<TPayload>(TPayload payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return MessagePackSerializer.ConvertFromJson(json);
    }

    public static TPayload DecodeMsgPack<TPayload>(ReadOnlySpan<byte> payload)
    {
        var json = MessagePackSerializer.ConvertToJson(payload.ToArray());
        return DecodeJson<TPayload>(System.Text.Encoding.UTF8.GetBytes(json));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    private static JsonElement NormalizePropertyNames(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteNormalizedElement(writer, element);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteNormalizedElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(NormalizePropertyName(property.Name));
                    WriteNormalizedElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteNormalizedElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string NormalizePropertyName(string name) =>
        name.Contains('_')
            ? name
            : JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}

/// <summary>
/// Helpers for mapping typed DTOs into common NWP/NCP response payload slots.
/// These are intentionally thin: frame-level routing and status semantics still
/// belong to the specific protocol operation.
/// </summary>
public static class NwpFramePayloadCodec
{
    public static JsonElement ToJsonElement<TPayload>(TPayload payload) =>
        NwpActionPayloadCodec.ToJsonElement(payload);

    public static TPayload ReadJsonElement<TPayload>(JsonElement payload) =>
        NwpActionPayloadCodec.ReadJsonElement<TPayload>(payload);

    public static CapsFrame ToCapsFrame<TPayload>(
        string anchorRef,
        TPayload payload,
        uint? tokenEst = null,
        string? tokenizerUsed = null,
        string? requestId = null) =>
        new()
        {
            AnchorRef = anchorRef,
            Count = 1,
            Data = [ToJsonElement(payload)],
            TokenEst = tokenEst,
            TokenizerUsed = tokenizerUsed,
            RequestId = requestId,
        };

    public static TPayload ReadCapsPayload<TPayload>(CapsFrame frame, int index = 0)
    {
        if (!TryReadCapsPayload(frame, out TPayload? payload, out var error, index))
        {
            throw new InvalidOperationException(error);
        }

        return payload!;
    }

    public static bool TryReadCapsPayload<TPayload>(
        CapsFrame frame,
        out TPayload? payload,
        out string? error,
        int index = 0)
    {
        payload = default;

        if (index < 0 || index >= frame.Data.Count)
        {
            error = $"CapsFrame data index {index} is outside the payload range 0..{frame.Data.Count - 1}.";
            return false;
        }

        return NwpActionPayloadCodec.TryReadJsonElement(
            frame.Data[index],
            $"CapsFrame data[{index}]",
            out payload,
            out error);
    }

    public static StreamFrame ToStreamFrame<TPayload>(
        string streamId,
        uint seq,
        bool isLast,
        IEnumerable<TPayload> payloads,
        string? anchorRef = null,
        uint? windowSize = null,
        string? errorCode = null) =>
        new()
        {
            StreamId = streamId,
            Seq = seq,
            IsLast = isLast || errorCode is not null,
            AnchorRef = anchorRef,
            Data = payloads.Select(ToJsonElement).ToArray(),
            WindowSize = windowSize,
            ErrorCode = errorCode,
        };

    public static IReadOnlyList<TPayload> ReadStreamPayloads<TPayload>(StreamFrame frame)
    {
        if (!TryReadStreamPayloads(frame, out IReadOnlyList<TPayload>? payloads, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return payloads!;
    }

    public static bool TryReadStreamPayloads<TPayload>(
        StreamFrame frame,
        out IReadOnlyList<TPayload>? payloads,
        out string? error)
    {
        var decoded = new List<TPayload>(frame.Data.Count);

        for (var i = 0; i < frame.Data.Count; i++)
        {
            if (!NwpActionPayloadCodec.TryReadJsonElement(
                    frame.Data[i],
                    $"StreamFrame data[{i}]",
                    out TPayload? payload,
                    out error))
            {
                payloads = null;
                return false;
            }

            decoded.Add(payload!);
        }

        payloads = decoded;
        error = null;
        return true;
    }

    public static TPayload ReadTaskResult<TPayload>(ActionTaskStatus status)
    {
        if (!TryReadTaskResult(status, out TPayload? payload, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return payload!;
    }

    public static bool TryReadTaskResult<TPayload>(
        ActionTaskStatus status,
        out TPayload? payload,
        out string? error)
    {
        payload = default;

        if (!string.Equals(status.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            error = $"ActionTaskStatus '{status.TaskId}' is '{status.Status}', not 'completed'.";
            return false;
        }

        return NwpActionPayloadCodec.TryReadJsonElement(
            status.Result,
            $"ActionTaskStatus '{status.TaskId}' result",
            out payload,
            out error);
    }

    public static TPayload ReadTaskError<TPayload>(ActionTaskStatus status)
    {
        if (!TryReadTaskError(status, out TPayload? payload, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return payload!;
    }

    public static bool TryReadTaskError<TPayload>(
        ActionTaskStatus status,
        out TPayload? payload,
        out string? error)
    {
        return NwpActionPayloadCodec.TryReadJsonElement(
            status.Error,
            $"ActionTaskStatus '{status.TaskId}' error",
            out payload,
            out error);
    }

    public static TPayload ReadErrorDetails<TPayload>(ErrorFrame frame)
    {
        if (!TryReadErrorDetails(frame, out TPayload? payload, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return payload!;
    }

    public static bool TryReadErrorDetails<TPayload>(
        ErrorFrame frame,
        out TPayload? payload,
        out string? error)
    {
        return NwpActionPayloadCodec.TryReadJsonElement(
            frame.Details,
            $"ErrorFrame '{frame.Error}' details",
            out payload,
            out error);
    }
}
