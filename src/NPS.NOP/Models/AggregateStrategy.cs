// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP.Models;

/// <summary>
/// SyncFrame result aggregation strategies (NPS-5 §3.3.2).
/// </summary>
public static class AggregateStrategy
{
    /// <summary>Merge all successful sub-task results into a single object (last-write-wins).</summary>
    public const string Merge = "merge";

    /// <summary>Take the first successful result.</summary>
    public const string First = "first";

    /// <summary>Keep all results as an array.</summary>
    public const string All = "all";

    /// <summary>Take the fastest <c>min_required</c> results in array format.</summary>
    public const string FastestK = "fastest_k";
}
