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

    /// <summary>
    /// Minimum required Agent assurance level
    /// (NPS-3 §5.1.1 / NPS-RFC-0003). When set, requests whose
    /// presented level is lower MUST be rejected with
    /// <c>NWP-AUTH-ASSURANCE-TOO-LOW</c>.
    /// <para>
    /// In Phase 1 of NPS-RFC-0003 the reference verifier carries this
    /// value through but does not enforce — enforcement is deferred to
    /// Phase 2 once all six SDKs implement the field.
    /// </para>
    /// </summary>
    public AssuranceLevel? MinAssuranceLevel { get; init; }
}
