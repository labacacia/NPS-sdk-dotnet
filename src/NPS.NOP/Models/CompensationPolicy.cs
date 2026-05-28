// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP.Models;

/// <summary>
/// Valid values for <c>TaskFrame.compensation_policy</c> (NPS-5 §3.5).
/// </summary>
public static class CompensationPolicy
{
    /// <summary>No saga rollback (default).</summary>
    public const string None      = "none";

    /// <summary>Run compensation for all completed nodes when the task fails.</summary>
    public const string OnFailure = "on_failure";

    /// <summary>Run compensation after both success and failure.</summary>
    public const string Always    = "always";
}
