// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace NPS.NOP.Models;

/// <summary>
/// Per-node retry policy (NPS-5 §3.1.4).
/// Delay formula: <c>min(initial_delay_ms * factor^attempt, max_delay_ms)</c>.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>Maximum retry attempts. Overrides TaskFrame.MaxRetries for this node.</summary>
    [JsonPropertyName("max_retries")]
    public byte MaxRetries { get; init; }

    /// <summary>Backoff strategy: <c>"fixed"</c>, <c>"linear"</c>, or <c>"exponential"</c> (default).</summary>
    [JsonPropertyName("backoff")]
    public string Backoff { get; init; } = BackoffStrategy.Exponential;

    /// <summary>Initial retry delay in milliseconds. Default 1000.</summary>
    [JsonPropertyName("initial_delay_ms")]
    public uint InitialDelayMs { get; init; } = 1000;

    /// <summary>Maximum delay cap in milliseconds. Default 30000.</summary>
    [JsonPropertyName("max_delay_ms")]
    public uint MaxDelayMs { get; init; } = 30000;

    /// <summary>Error codes that trigger retry. Null means retry on all failures.</summary>
    [JsonPropertyName("retry_on")]
    public IReadOnlyList<string>? RetryOn { get; init; }

    /// <summary>
    /// Computes the delay for a given attempt number (0-based).
    /// </summary>
    public uint ComputeDelayMs(int attempt)
    {
        double factor = Backoff switch
        {
            BackoffStrategy.Fixed => 1,
            BackoffStrategy.Linear => attempt + 1,
            _ => Math.Pow(2, attempt),
        };

        var delay = (uint)Math.Min(InitialDelayMs * factor, MaxDelayMs);
        return delay;
    }
}

/// <summary>
/// Well-known backoff strategy identifiers (NPS-5 §3.1.4).
/// </summary>
public static class BackoffStrategy
{
    public const string Fixed = "fixed";
    public const string Linear = "linear";
    public const string Exponential = "exponential";
}
