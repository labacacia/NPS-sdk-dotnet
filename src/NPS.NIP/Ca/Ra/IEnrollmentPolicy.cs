// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Ca.Ra;

/// <summary>
/// Gate that must pass before a NIP CA issues an IdentFrame (NPS-CR-0005 §3).
/// Implementations correspond to the three enrollment tiers:
/// allowlist (T1), bootstrap token (T2), pending queue (T3).
/// </summary>
public interface IEnrollmentPolicy
{
    /// <summary>
    /// Checks whether <paramref name="identifier"/> is permitted to enroll.
    /// </summary>
    /// <param name="entityType"><c>"agent"</c> or <c>"node"</c>.</param>
    /// <param name="identifier">Identifier portion of the requested NID.</param>
    /// <param name="pubKey">Public key in <c>ed25519:&lt;base64url&gt;</c> format.</param>
    /// <param name="capabilities">Requested capability list.</param>
    /// <param name="scopeJson">Scope JSON object.</param>
    /// <param name="metadataJson">Optional metadata JSON.</param>
    /// <param name="enrollmentToken">
    /// Bootstrap token supplied by the caller (Tier 2), or <c>null</c>.
    /// </param>
    /// <exception cref="NipCaException">
    /// Thrown when the enrollment is denied. The <c>ErrorCode</c> is one of
    /// <c>NIP-RA-TOKEN-INVALID</c>, <c>NIP-RA-TOKEN-EXPIRED</c>,
    /// <c>NIP-RA-NID-NOT-ALLOWED</c>.
    /// </exception>
    /// <exception cref="NipRaPendingException">
    /// Thrown by Tier 3 to signal that the request has been queued and the
    /// caller should receive <c>202 Accepted</c> with a pending ID.
    /// </exception>
    Task CheckAsync(
        string                 entityType,
        string                 identifier,
        string                 pubKey,
        IReadOnlyList<string>  capabilities,
        string                 scopeJson,
        string?                metadataJson,
        string?                enrollmentToken,
        CancellationToken      ct);
}

/// <summary>
/// Thrown by <see cref="IEnrollmentPolicy.CheckAsync"/> when a Tier-3
/// (pending queue) policy enqueues the registration request.
/// The router translates this into a <c>202 Accepted</c> response.
/// </summary>
public sealed class NipRaPendingException : Exception
{
    /// <summary>Opaque identifier of the queued record.</summary>
    public string PendingId { get; }

    public NipRaPendingException(string pendingId)
        : base($"Registration queued with pending id: {pendingId}")
        => PendingId = pendingId;
}
