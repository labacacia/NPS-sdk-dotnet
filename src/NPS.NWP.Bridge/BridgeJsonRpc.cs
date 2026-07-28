// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NWP.Bridge;

/// <summary>JSON-RPC 2.0 request envelope used by MCP and A2A Bridge servers.</summary>
public sealed record BridgeJsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    /// <summary>Request id. <c>null</c> indicates a notification.</summary>
    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

/// <summary>JSON-RPC 2.0 response envelope used by MCP and A2A Bridge servers.</summary>
public sealed record BridgeJsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public BridgeJsonRpcError? Error { get; init; }
}

/// <summary>JSON-RPC 2.0 error object.</summary>
public sealed record BridgeJsonRpcError
{
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}

/// <summary>Standard JSON-RPC error codes plus Bridge server application codes.</summary>
public static class BridgeJsonRpcErrorCodes
{
    // ── Standard JSON-RPC 2.0 codes ──────────────────────────────────────────
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    // ── Application-defined codes (JSON-RPC reserves -32000..-32099) ─────────
    // The NPS status each maps from is normative — see NWP §16.3 and BridgeErrorMap.

    /// <summary>The upstream external service failed or was unreachable.</summary>
    public const int UpstreamError = -32000;

    /// <summary>Maps from <c>NPS-AUTH-UNAUTHENTICATED</c>.</summary>
    public const int Unauthenticated = -32001;

    /// <summary>Maps from <c>NPS-AUTH-FORBIDDEN</c>.</summary>
    public const int Forbidden = -32003;

    /// <summary>Maps from <c>NPS-CLIENT-CONFLICT</c>.</summary>
    public const int Conflict = -32004;

    /// <summary>Maps from <c>NPS-LIMIT-RATE</c> / <c>-BUDGET</c> / <c>-PAYLOAD</c>.</summary>
    public const int LimitExceeded = -32005;

    /// <summary>
    /// Retired by NPS-CR-0010. An unknown tool maps to <see cref="MethodNotFound"/> (-32601)
    /// per NWP §16.3 — that is what an MCP client already understands. The value -32002 is
    /// left reserved rather than reused, so a client pinned to the alpha.15 behaviour cannot
    /// silently misread a different error as a missing tool.
    /// </summary>
    [Obsolete("Unknown tool maps to MethodNotFound (-32601) per NWP §16.3 (NPS-CR-0010). Do not emit.")]
    public const int ToolNotFound = -32002;
}

/// <summary>JSON-RPC 2.0 envelope helpers shared by the Bridge core and its ASP.NET hosting layer.</summary>
public static class BridgeJsonRpc
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static BridgeJsonRpcResponse Success(BridgeJsonRpcRequest request, object result) => new()
    {
        Id = Clone(request.Id),
        Result = JsonSerializer.SerializeToElement(result, Json),
    };

    public static BridgeJsonRpcResponse Error(
        BridgeJsonRpcRequest request,
        int code,
        string message,
        object? data = null) =>
        Error(Clone(request.Id), code, message, data);

    public static BridgeJsonRpcResponse Error(
        JsonElement? id,
        int code,
        string message,
        object? data = null) => new()
    {
        Id = Clone(id),
        Error = new BridgeJsonRpcError
        {
            Code = code,
            Message = message,
            Data = data is null ? null : JsonSerializer.SerializeToElement(data, Json),
        },
    };

    public static JsonElement? Clone(JsonElement? element) =>
        element.HasValue ? element.Value.Clone() : null;
}
