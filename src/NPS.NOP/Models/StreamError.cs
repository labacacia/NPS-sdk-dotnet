// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace NPS.NOP.Models;

/// <summary>
/// Error payload carried by an AlignStream final frame (NPS-5 §3.4).
/// </summary>
public sealed record StreamError
{
    /// <summary>NOP error code (e.g. <c>NOP-TASK-TIMEOUT</c>).</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>Human-readable error description.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Whether the caller may retry this operation.</summary>
    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }
}
