// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NOP.Models;

/// <summary>
/// Transparent context carried across all sub-tasks (NPS-5 §3.1.2).
/// Supports OpenTelemetry W3C TraceContext for distributed tracing.
/// </summary>
public sealed record TaskContext
{
    /// <summary>Agent session identifier (reused across requests).</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    /// <summary>OpenTelemetry Trace ID (16 bytes hex, 32 characters).</summary>
    [JsonPropertyName("trace_id")]
    public string? TraceId { get; init; }

    /// <summary>Current Span ID (8 bytes hex, 16 characters).</summary>
    [JsonPropertyName("span_id")]
    public string? SpanId { get; init; }

    /// <summary>OpenTelemetry Trace Flags (e.g. 0x01 = sampled).</summary>
    [JsonPropertyName("trace_flags")]
    public byte? TraceFlags { get; init; }

    /// <summary>OpenTelemetry Baggage key-value pairs, propagated to all sub-tasks.</summary>
    [JsonPropertyName("baggage")]
    public IReadOnlyDictionary<string, string>? Baggage { get; init; }

    /// <summary>Application-defined context. NOP does not inspect this field.</summary>
    [JsonPropertyName("custom")]
    public JsonElement? Custom { get; init; }
}
