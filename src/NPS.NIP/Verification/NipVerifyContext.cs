// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Verification;

/// <summary>
/// Per-request context passed to <see cref="NipIdentVerifier"/> for NPS-3 §7 steps 5–6.
/// All fields are optional — omit to skip the corresponding check.
/// </summary>
public sealed record NipVerifyContext
{
    /// <summary>
    /// Capabilities the Node requires the Agent to hold (Step 5).
    /// E.g. <c>["nwp:query"]</c>.
    /// When null or empty, the capability check is skipped.
    /// </summary>
    public IReadOnlyList<string>? RequiredCapabilities { get; init; }

    /// <summary>
    /// The full NWP node path the Agent is trying to access (Step 6).
    /// E.g. <c>"nwp://api.myapp.com/products"</c>.
    /// When null, the scope check is skipped.
    /// </summary>
    public string? TargetNodePath { get; init; }

    /// <summary>
    /// Clock override for testing (replaces <c>DateTime.UtcNow</c> in expiry check).
    /// Leave null in production.
    /// </summary>
    public DateTime? AsOf { get; init; }
}
