// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Ca;

/// <summary>
/// Enrollment-tier selector for the NIP CA Registration Authority model
/// (NPS-CR-0005 §3). Governs which gate an inbound registration request
/// must pass before the CA issues an IdentFrame.
/// </summary>
public enum EnrollmentTier
{
    /// <summary>
    /// Tier 1: Operator pre-configures a glob allowlist.
    /// Registrations whose <c>identifier</c> matches at least one pattern are
    /// admitted immediately; all others are rejected with
    /// <c>NIP-RA-NID-NOT-ALLOWED</c>. Default tier.
    /// </summary>
    Allowlist = 1,

    /// <summary>
    /// Tier 2: Caller presents a single-use bootstrap token
    /// (prefix <c>nps-bootstrap-</c>) obtained via
    /// <c>POST /v1/enrollment/tokens</c>. The token is validated and consumed
    /// atomically; invalid or expired tokens are rejected with
    /// <c>NIP-RA-TOKEN-INVALID</c> / <c>NIP-RA-TOKEN-EXPIRED</c>.
    /// </summary>
    BootstrapToken = 2,

    /// <summary>
    /// Tier 3: All registrations are queued as pending records.
    /// The CA returns <c>202 Accepted</c> with a <c>pending_id</c>; an
    /// operator must approve or reject via
    /// <c>POST /v1/enrollment/pending/{id}/approve</c> or <c>/reject</c>.
    /// Rejected records surface the <c>NIP-RA-PENDING-REJECTED</c> error code.
    /// </summary>
    PendingQueue = 3,
}
