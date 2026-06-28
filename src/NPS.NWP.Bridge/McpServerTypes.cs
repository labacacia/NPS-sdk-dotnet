// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NWP.Bridge;

/// <summary>MCP protocol version implemented by the Bridge server adapter.</summary>
public static class McpServerProtocol
{
    public const string Version = "2024-11-05";
}

public sealed record McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; init; } = McpServerProtocol.Version;

    [JsonPropertyName("serverInfo")]
    public required McpServerInfo ServerInfo { get; init; }

    [JsonPropertyName("capabilities")]
    public required McpServerCapabilities Capabilities { get; init; }
}

public sealed record McpServerInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

public sealed record McpServerCapabilities
{
    [JsonPropertyName("tools")]
    public McpToolCapabilities? Tools { get; init; }
}

public sealed record McpToolCapabilities
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; init; }
}

public sealed record McpTool
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("inputSchema")]
    public required JsonElement InputSchema { get; init; }
}

public sealed record McpToolListResult
{
    [JsonPropertyName("tools")]
    public required IReadOnlyList<McpTool> Tools { get; init; }
}

public sealed record McpToolCallParams
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }
}

public sealed record McpToolCallResult
{
    [JsonPropertyName("content")]
    public required IReadOnlyList<McpContent> Content { get; init; }

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }
}

public sealed record McpContent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}
