// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Ca;

/// <summary>
/// Persisted record of a NIP certificate (NPS-3 §5.1).
/// Stored in the <c>nip_certificates</c> table.
/// </summary>
public sealed class NipCertRecord
{
    public required string   Nid          { get; init; }
    public required string   EntityType   { get; init; }   // "agent" | "node" | "operator"
    public required string   Serial       { get; init; }
    public required string   PubKey       { get; init; }
    public required string[] Capabilities { get; init; }
    public required string   ScopeJson    { get; init; }   // JSON blob
    public required string   IssuedBy     { get; init; }
    public required DateTime IssuedAt     { get; init; }
    public required DateTime ExpiresAt    { get; init; }
    public          DateTime? RevokedAt   { get; init; }
    public          string?  RevokeReason { get; init; }
    public          string?  MetadataJson { get; init; }  // JSON blob, nullable

    /// <summary>
    /// Lineage role (NPS-CR-0003 §5.1.3). One of <c>"group"</c>,
    /// <c>"session"</c>, or <c>null</c> for ordinary single-NID agents.
    /// </summary>
    public string?  NidRole      { get; init; }

    /// <summary>
    /// Immediate parent NID (NPS-CR-0003 §5.1.3). Set on session records
    /// pointing to the group NID; null on group and ordinary agent records.
    /// Indexed for cascade revocation (`O(group fan-out)` lookup).
    /// </summary>
    public string?  ParentNid    { get; init; }

    /// <summary>
    /// Full signed lineage object as canonical JSON (NPS-CR-0003 §5.1.3).
    /// Persisted verbatim so reissues / audit can reproduce the exact
    /// canonical form that was signed.
    /// </summary>
    public string?  LineageJson  { get; init; }
}

/// <summary>
/// Persistence abstraction for NIP CA certificate storage (NPS-3 §8).
/// </summary>
public interface INipCaStore
{
    /// <summary>
    /// Saves a newly issued certificate record.
    /// Throws if the NID or serial already exists.
    /// </summary>
    Task SaveAsync(NipCertRecord record, CancellationToken ct = default);

    /// <summary>Returns the certificate record for <paramref name="nid"/>, or null if not found.</summary>
    Task<NipCertRecord?> GetByNidAsync(string nid, CancellationToken ct = default);

    /// <summary>Returns the certificate record for <paramref name="serial"/>, or null if not found.</summary>
    Task<NipCertRecord?> GetBySerialAsync(string serial, CancellationToken ct = default);

    /// <summary>
    /// Marks a certificate as revoked.
    /// Returns false if the NID was not found.
    /// </summary>
    Task<bool> RevokeAsync(string nid, string reason, DateTime revokedAt, CancellationToken ct = default);

    /// <summary>
    /// Generates and reserves the next unique serial number.
    /// Returns a hex string e.g. <c>0x0A3F9C</c>.
    /// Implementation MUST be atomic (sequence or CAS).
    /// </summary>
    Task<string> NextSerialAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all revoked certificates (for CRL generation).
    /// </summary>
    Task<IReadOnlyList<NipCertRecord>> GetRevokedAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns every record whose <see cref="NipCertRecord.ParentNid"/>
    /// equals <paramref name="parentNid"/>. Used by
    /// <c>NipCaService.RevokeAsync</c> to enumerate live sessions whose
    /// group has been revoked, and by the audit
    /// <c>GET /v1/orchestrators/groups/{nid}/sessions</c> endpoint
    /// (NPS-CR-0003 §5.1.3 / §8).
    /// </summary>
    Task<IReadOnlyList<NipCertRecord>> GetByParentNidAsync(string parentNid, CancellationToken ct = default);
}
