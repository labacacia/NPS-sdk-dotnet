// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP.Orchestration;

/// <summary>
/// Result returned by a Worker Agent in response to a preflight probe (NPS-5 §4.3).
/// </summary>
public sealed record PreflightResult
{
    /// <summary>NID of the responding Worker Agent.</summary>
    public required string AgentNid { get; init; }

    /// <summary>True when the agent can accept the delegated workload.</summary>
    public required bool Available { get; init; }

    /// <summary>NPT budget the agent can commit. Null when unavailable.</summary>
    public long? AvailableNpt { get; init; }

    /// <summary>Estimated queue depth in milliseconds. Null when unavailable.</summary>
    public int? EstimatedQueueMs { get; init; }

    /// <summary>Capability identifiers the agent supports.</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Human-readable reason when <see cref="Available"/> is false.</summary>
    public string? UnavailableReason { get; init; }
}
