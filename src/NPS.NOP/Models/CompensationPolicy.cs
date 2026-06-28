// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP.Models;

/// <summary>
/// Valid values for <c>TaskFrame.compensation_policy</c> (NPS-5 §3.5).
/// </summary>
public static class CompensationPolicy
{
    /// <summary>Run compensation for completed predecessors when the task fails; compensation failures are reported but do not stop remaining compensation.</summary>
    public const string BestEffort = "best_effort";

    /// <summary>Run compensation for completed predecessors when the task fails; missing or failed compensation is terminal.</summary>
    public const string Strict = "strict";

    /// <summary>Legacy alias: no saga rollback. Not a NPS-5 wire value.</summary>
    public const string None = "none";

    /// <summary>Legacy alias for <see cref="BestEffort"/>.</summary>
    public const string OnFailure = "on_failure";

    /// <summary>Non-standard extension: run compensation after both success and failure.</summary>
    public const string Always    = "always";

    /// <summary>Returns true when the policy runs compensation after a task failure.</summary>
    public static bool RunsOnFailure(string? policy) =>
        policy is BestEffort or Strict or OnFailure or Always;

    /// <summary>Returns true when the policy runs compensation after a successful task.</summary>
    public static bool RunsOnSuccess(string? policy) => policy == Always;

    /// <summary>Returns true when any missing or failed compensation step is terminal.</summary>
    public static bool IsStrict(string? policy) => policy == Strict;
}
