// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NWP.Bridge;

/// <summary>A2A protocol version implemented by the Bridge server adapter.</summary>
public static class A2aServerProtocol
{
    public const string Version = "0.2";
}

public sealed record A2aAgentCard
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("provider")]
    public A2aAgentProvider? Provider { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("capabilities")]
    public required A2aAgentCapabilities Capabilities { get; init; }

    [JsonPropertyName("authentication")]
    public A2aAgentAuthentication? Authentication { get; init; }

    [JsonPropertyName("defaultInputModes")]
    public IReadOnlyList<string> DefaultInputModes { get; init; } = new[] { "text", "data" };

    [JsonPropertyName("defaultOutputModes")]
    public IReadOnlyList<string> DefaultOutputModes { get; init; } = new[] { "text", "data" };

    [JsonPropertyName("skills")]
    public required IReadOnlyList<A2aAgentSkill> Skills { get; init; }
}

public sealed record A2aAgentProvider
{
    [JsonPropertyName("organization")]
    public required string Organization { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed record A2aAgentCapabilities
{
    [JsonPropertyName("streaming")]
    public bool Streaming { get; init; }

    [JsonPropertyName("pushNotifications")]
    public bool PushNotifications { get; init; }

    [JsonPropertyName("stateTransitionHistory")]
    public bool StateTransitionHistory { get; init; }
}

public sealed record A2aAgentAuthentication
{
    [JsonPropertyName("schemes")]
    public required IReadOnlyList<string> Schemes { get; init; }

    [JsonPropertyName("credentials")]
    public string? Credentials { get; init; }
}

public sealed record A2aAgentSkill
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("inputModes")]
    public IReadOnlyList<string>? InputModes { get; init; }

    [JsonPropertyName("outputModes")]
    public IReadOnlyList<string>? OutputModes { get; init; }
}

public static class A2aTaskState
{
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public sealed record A2aTask
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("status")]
    public required A2aTaskStatus Status { get; init; }

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<A2aArtifact>? Artifacts { get; init; }

    [JsonPropertyName("history")]
    public IReadOnlyList<A2aMessage>? History { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

public sealed record A2aTaskStatus
{
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("message")]
    public A2aMessage? Message { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }
}

public sealed record A2aMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("parts")]
    public required IReadOnlyList<A2aPart> Parts { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

public sealed record A2aPart
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

public sealed record A2aArtifact
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("parts")]
    public required IReadOnlyList<A2aPart> Parts { get; init; }

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

public sealed record A2aSendTaskParams
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("message")]
    public required A2aMessage Message { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}
