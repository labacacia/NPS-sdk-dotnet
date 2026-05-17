// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Ca.Ra;

/// <summary>
/// Store for pending registration requests awaiting operator approval
/// (NPS-CR-0005 §3.4).
/// </summary>
public interface IPendingStore
{
    /// <summary>Enqueues a new pending registration and returns its ID.</summary>
    Task<string> EnqueueAsync(PendingRegistration request, CancellationToken ct);

    /// <summary>Lists all pending registrations (all statuses).</summary>
    Task<IReadOnlyList<PendingRegistration>> ListAsync(CancellationToken ct);

    /// <summary>Returns a single pending registration by ID, or <c>null</c>.</summary>
    Task<PendingRegistration?> GetAsync(string id, CancellationToken ct);

    /// <summary>
    /// Transitions a <c>Pending</c> record to <c>Approved</c>.
    /// Returns <c>false</c> if the ID was not found or not in Pending status.
    /// </summary>
    Task<bool> ApproveAsync(string id, CancellationToken ct);

    /// <summary>
    /// Transitions a <c>Pending</c> record to <c>Rejected</c>.
    /// Returns <c>false</c> if the ID was not found or not in Pending status.
    /// </summary>
    Task<bool> RejectAsync(string id, string reason, CancellationToken ct);

    /// <summary>Count of records currently in <c>Pending</c> status.</summary>
    int PendingCount { get; }
}

/// <summary>A registration request waiting for operator approval.</summary>
public sealed record PendingRegistration(
    string                 Id,
    string                 EntityType,
    string                 Identifier,
    string                 PubKey,
    IReadOnlyList<string>  Capabilities,
    string                 ScopeJson,
    string?                MetadataJson,
    DateTimeOffset         RequestedAt,
    PendingStatus          Status,
    string?                RejectReason);

/// <summary>Lifecycle state of a pending registration record.</summary>
public enum PendingStatus
{
    Pending  = 0,
    Approved = 1,
    Rejected = 2,
}
