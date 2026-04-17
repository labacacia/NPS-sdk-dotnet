// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;

namespace NPS.NIP.Ca;

/// <summary>
/// Core CA business logic: issue, renew, revoke, and verify NID certificates (NPS-3 §6–8).
/// All signing is done with the CA's Ed25519 private key loaded via <see cref="NipKeyManager"/>.
/// </summary>
public sealed class NipCaService
{
    private readonly NipCaOptions  _opts;
    private readonly INipCaStore   _store;
    private readonly NipKeyManager _keys;

    public NipCaService(NipCaOptions opts, INipCaStore store, NipKeyManager keys)
    {
        _opts  = opts;
        _store = store;
        _keys  = keys;
    }

    // ── Register (Agent / Node) ───────────────────────────────────────────────

    /// <summary>
    /// Registers a new Agent or Node, issues an IdentFrame, and persists the record.
    /// </summary>
    /// <param name="entityType"><c>"agent"</c> or <c>"node"</c>.</param>
    /// <param name="identifier">Unique identifier portion of the NID (e.g. UUID or node path).</param>
    /// <param name="pubKey">Agent/Node public key in <c>ed25519:{base64url}</c> format.</param>
    /// <param name="capabilities">Requested capability list.</param>
    /// <param name="scopeJson">Scope JSON object.</param>
    /// <param name="metadataJson">Optional metadata JSON object.</param>
    public async Task<IdentFrame> RegisterAsync(
        string            entityType,
        string            identifier,
        string            pubKey,
        IReadOnlyList<string> capabilities,
        string            scopeJson,
        string?           metadataJson = null,
        CancellationToken ct           = default)
    {
        var nid      = BuildNid(entityType, identifier);
        var existing = await _store.GetByNidAsync(nid, ct);
        if (existing is not null)
            throw new NipCaException($"NID already exists: {nid}", NipErrorCodes.NidAlreadyExists);

        var validDays = entityType == "node" ? _opts.NodeCertValidityDays : _opts.AgentCertValidityDays;
        var now       = DateTime.UtcNow;
        var expiresAt = now.AddDays(validDays);
        var serial    = await _store.NextSerialAsync(ct);

        var frame = IssueFrame(nid, pubKey, capabilities, scopeJson, now, expiresAt, serial, metadataJson);

        var record = new NipCertRecord
        {
            Nid          = nid,
            EntityType   = entityType,
            Serial       = serial,
            PubKey       = pubKey,
            Capabilities = capabilities.ToArray(),
            ScopeJson    = scopeJson,
            IssuedBy     = _opts.CaNid,
            IssuedAt     = now,
            ExpiresAt    = expiresAt,
            MetadataJson = metadataJson,
        };
        await _store.SaveAsync(record, ct);

        return frame;
    }

    // ── Renew ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renews a certificate. Only allowed within the renewal window
    /// (<see cref="NipCaOptions.RenewalWindowDays"/> before expiry).
    /// Returns a new IdentFrame with a fresh serial and extended expiry.
    /// </summary>
    public async Task<IdentFrame> RenewAsync(string nid, CancellationToken ct = default)
    {
        var record = await _store.GetByNidAsync(nid, ct)
            ?? throw new NipCaException($"NID not found: {nid}", NipErrorCodes.NidNotFound);

        if (record.RevokedAt.HasValue)
            throw new NipCaException($"NID is revoked: {nid}", NipErrorCodes.CertRevoked);

        var now            = DateTime.UtcNow;
        var renewWindowEnd = record.ExpiresAt;
        var renewWindowStart = record.ExpiresAt.AddDays(-_opts.RenewalWindowDays);

        if (now < renewWindowStart)
            throw new NipCaException(
                $"Renewal window opens {renewWindowStart:O}. Too early to renew.",
                NipErrorCodes.RenewalTooEarly);

        var validDays = record.EntityType == "node" ? _opts.NodeCertValidityDays : _opts.AgentCertValidityDays;
        var expiresAt = now.AddDays(validDays);
        var serial    = await _store.NextSerialAsync(ct);

        var frame = IssueFrame(nid, record.PubKey, record.Capabilities, record.ScopeJson,
            now, expiresAt, serial, record.MetadataJson);

        // Save new record (old one stays for audit, new one replaces active cert)
        var newRecord = new NipCertRecord
        {
            Nid          = nid,
            EntityType   = record.EntityType,
            Serial       = serial,
            PubKey       = record.PubKey,
            Capabilities = record.Capabilities,
            ScopeJson    = record.ScopeJson,
            IssuedBy     = _opts.CaNid,
            IssuedAt     = now,
            ExpiresAt    = expiresAt,
            MetadataJson = record.MetadataJson,
        };
        await _store.SaveAsync(newRecord, ct);

        return frame;
    }

    // ── Revoke ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Revokes a certificate immediately and returns the signed RevokeFrame.
    /// </summary>
    public async Task<RevokeFrame> RevokeAsync(string nid, string reason, CancellationToken ct = default)
    {
        var record = await _store.GetByNidAsync(nid, ct)
            ?? throw new NipCaException($"NID not found: {nid}", NipErrorCodes.NidNotFound);

        var now      = DateTime.UtcNow;
        var revoked  = await _store.RevokeAsync(nid, reason, now, ct);
        if (!revoked)
            throw new NipCaException($"Failed to revoke {nid}.", NipErrorCodes.NidNotFound);

        // Build RevokeFrame for signing (signature excluded from canonical form)
        var payload = new
        {
            frame      = "0x22",
            target_nid = nid,
            serial     = record.Serial,
            reason,
            revoked_at = now.ToString("O"),
        };
        var signature = NipSigner.Sign(_keys.PrivateKey, payload);

        return new RevokeFrame
        {
            TargetNid = nid,
            Serial    = record.Serial,
            Reason    = reason,
            RevokedAt = now.ToString("O"),
            Signature = signature,
        };
    }

    // ── Verify (OCSP) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies a NID: checks existence, expiry, revocation status, and signature.
    /// Returns a <see cref="NipVerifyResult"/> describing the outcome.
    /// </summary>
    public async Task<NipVerifyResult> VerifyAsync(string nid, CancellationToken ct = default)
    {
        var record = await _store.GetByNidAsync(nid, ct);
        if (record is null)
            return NipVerifyResult.Fail(NipErrorCodes.NidNotFound, "NID not found.");

        if (record.RevokedAt.HasValue)
            return NipVerifyResult.Fail(NipErrorCodes.CertRevoked,
                $"Revoked at {record.RevokedAt:O}: {record.RevokeReason}");

        if (DateTime.UtcNow > record.ExpiresAt)
            return NipVerifyResult.Fail(NipErrorCodes.CertExpired,
                $"Expired at {record.ExpiresAt:O}.");

        return NipVerifyResult.Ok(record);
    }

    // ── CRL ───────────────────────────────────────────────────────────────────

    /// <summary>Returns the current Certificate Revocation List (NPS-3 §8).</summary>
    public Task<IReadOnlyList<NipCertRecord>> GetCrlAsync(CancellationToken ct = default) =>
        _store.GetRevokedAsync(ct);

    // ── CA public key ─────────────────────────────────────────────────────────

    /// <summary>Returns the CA public key in <c>ed25519:{base64url}</c> format.</summary>
    public string GetCaPublicKey() => NipSigner.EncodePublicKey(_keys.PublicKey);

    // ── NID builder ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a NID from the CA's issuer domain and an entity-specific identifier.
    /// </summary>
    public string BuildNid(string entityType, string identifier)
    {
        // Extract domain from CaNid: "urn:nps:org:ca.example.com" → "ca.example.com"
        var parts  = _opts.CaNid.Split(':');
        var domain = parts.Length >= 4 ? parts[3] : _opts.CaNid;
        return $"urn:nps:{entityType}:{domain}:{identifier}";
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private IdentFrame IssueFrame(
        string nid, string pubKey,
        IReadOnlyList<string> capabilities,
        string scopeJson,
        DateTime issuedAt, DateTime expiresAt,
        string serial,
        string? metadataJson)
    {
        var scope   = JsonDocument.Parse(scopeJson).RootElement;
        var issuedAtStr  = issuedAt.ToString("O");
        var expiresAtStr = expiresAt.ToString("O");

        // Canonical payload for signing (alphabetical key order, no metadata)
        var payload = new
        {
            capabilities,
            expires_at = expiresAtStr,
            frame      = "0x20",
            issued_at  = issuedAtStr,
            issued_by  = _opts.CaNid,
            nid,
            pub_key    = pubKey,
            scope,
            serial,
        };
        var signature = NipSigner.Sign(_keys.PrivateKey, payload);

        IdentMetadata? metadata = null;
        if (metadataJson is not null)
            metadata = JsonSerializer.Deserialize<IdentMetadata>(metadataJson, s_jsonOpts);

        return new IdentFrame
        {
            Nid          = nid,
            PubKey       = pubKey,
            Capabilities = capabilities,
            Scope        = scope.Clone(),
            IssuedBy     = _opts.CaNid,
            IssuedAt     = issuedAtStr,
            ExpiresAt    = expiresAtStr,
            Serial       = serial,
            Signature    = signature,
            Metadata     = metadata,
        };
    }

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

// ── Result types ──────────────────────────────────────────────────────────────

/// <summary>Result of a NIP certificate verification check.</summary>
public sealed class NipVerifyResult
{
    public bool           Valid     { get; private init; }
    public string?        ErrorCode { get; private init; }
    public string?        Message   { get; private init; }
    public NipCertRecord? Record    { get; private init; }

    public static NipVerifyResult Ok(NipCertRecord record) =>
        new() { Valid = true, Record = record };

    public static NipVerifyResult Fail(string errorCode, string message) =>
        new() { Valid = false, ErrorCode = errorCode, Message = message };
}

/// <summary>Thrown when a NIP CA operation cannot be completed.</summary>
public sealed class NipCaException : Exception
{
    public string ErrorCode { get; }
    public NipCaException(string message, string errorCode) : base(message) => ErrorCode = errorCode;
}

/// <summary>NIP error codes (NPS-3 §9).</summary>
public static class NipErrorCodes
{
    public const string CertExpired      = "NIP-CERT-EXPIRED";
    public const string CertRevoked      = "NIP-CERT-REVOKED";
    public const string CertSigInvalid   = "NIP-CERT-SIGNATURE-INVALID";
    public const string CertUntrusted    = "NIP-CERT-UNTRUSTED-ISSUER";
    public const string CertCapMissing   = "NIP-CERT-CAPABILITY-MISSING";
    public const string CertScope        = "NIP-CERT-SCOPE-VIOLATION";
    public const string NidNotFound      = "NIP-CA-NID-NOT-FOUND";
    public const string NidAlreadyExists = "NIP-CA-NID-ALREADY-EXISTS";
    public const string SerialDuplicate  = "NIP-CA-SERIAL-DUPLICATE";
    public const string RenewalTooEarly  = "NIP-CA-RENEWAL-TOO-EARLY";
    public const string ScopeExpansion   = "NIP-CA-SCOPE-EXPANSION-DENIED";
    public const string OcspUnavailable  = "NIP-OCSP-UNAVAILABLE";
    public const string TrustInvalid     = "NIP-TRUST-FRAME-INVALID";
}
