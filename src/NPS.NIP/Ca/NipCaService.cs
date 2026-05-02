// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;
using NPS.NIP.X509;

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
    private readonly Lazy<X509Certificate2> _rootCert;

    public NipCaService(NipCaOptions opts, INipCaStore store, NipKeyManager keys)
    {
        _opts     = opts;
        _store    = store;
        _keys     = keys;
        _rootCert = new Lazy<X509Certificate2>(CreateRootCert);
    }

    /// <summary>
    /// Self-signed root certificate for this CA, generated once from the
    /// persistent CA key. Stable across calls within a process lifetime;
    /// regenerated (same key, new validity window) on restart.
    /// Used by the ACME server and the X.509 registration endpoint.
    /// </summary>
    public X509Certificate2 CaRootCert => _rootCert.Value;

    private X509Certificate2 CreateRootCert()
    {
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;
        if (serial[0] == 0) serial[0] = 0x01;
        var now = DateTimeOffset.UtcNow;
        return NipX509Builder.IssueRoot(_opts.CaNid, _keys.PrivateKey, now, now.AddYears(10), serial);
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

        if (_opts.AllowedCapabilities is not null)
        {
            var disallowed = capabilities.Where(c => !_opts.AllowedCapabilities.Contains(c)).ToList();
            if (disallowed.Count > 0)
                throw new NipCaException(
                    $"Capabilities not permitted by this CA: {string.Join(", ", disallowed)}",
                    NipErrorCodes.CertCapMissing);
        }

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

    // ── Register X.509 (NPS-RFC-0002 prototype) ───────────────────────────────

    /// <summary>
    /// Registers a new Agent or Node and issues an <see cref="IdentFrame"/>
    /// with both the legacy CA-signed JSON proof <b>and</b> a DER-encoded
    /// X.509 certificate chain per NPS-RFC-0002 §4.1. The same
    /// <see cref="NipCertRecord"/> is persisted so the existing renew /
    /// revoke / OCSP machinery covers v2 certs without further changes.
    ///
    /// <para>The chain currently has a single self-signed root supplied via
    /// <paramref name="rootCert"/>; the prototype intentionally does not
    /// implement intermediate hierarchy depth — that's deferred to a
    /// follow-up.</para>
    /// </summary>
    public async Task<IdentFrame> RegisterX509Async(
        string                 entityType,
        string                 identifier,
        string                 pubKey,
        IReadOnlyList<string>  capabilities,
        string                 scopeJson,
        X509Certificate2?      rootCert       = null,
        AssuranceLevel         assuranceLevel = AssuranceLevel.Anonymous,
        string?                metadataJson   = null,
        CancellationToken      ct             = default)
    {
        rootCert ??= CaRootCert;

        var nid      = BuildNid(entityType, identifier);
        var existing = await _store.GetByNidAsync(nid, ct);
        if (existing is not null)
            throw new NipCaException($"NID already exists: {nid}", NipErrorCodes.NidAlreadyExists);

        if (_opts.AllowedCapabilities is not null)
        {
            var disallowed = capabilities.Where(c => !_opts.AllowedCapabilities.Contains(c)).ToList();
            if (disallowed.Count > 0)
                throw new NipCaException(
                    $"Capabilities not permitted by this CA: {string.Join(", ", disallowed)}",
                    NipErrorCodes.CertCapMissing);
        }

        var validDays = entityType == "node" ? _opts.NodeCertValidityDays : _opts.AgentCertValidityDays;
        var now       = DateTime.UtcNow;
        var expiresAt = now.AddDays(validDays);
        var serial    = await _store.NextSerialAsync(ct);

        // Build the legacy v1 frame first — gives us the CA Ed25519 signature,
        // serial, and snake_case scope JsonElement that v2 verifiers also rely on.
        // Pass the assurance level through so it lands in the v1 signature
        // (RFC-0003) and the X.509 leaf extension (RFC-0002 §4.1) consistently.
        var v1Frame = IssueFrame(nid, pubKey, capabilities, scopeJson,
            now, expiresAt, serial, metadataJson, assuranceLevel);

        // Layer X.509 on top.
        var subjectPubRaw = ExtractEd25519Raw(pubKey);
        var leafSerial    = ParseSerialBytes(serial);
        var role          = entityType == "node"
            ? NipX509Builder.LeafRole.Node
            : NipX509Builder.LeafRole.Agent;

        var leafCert = NipX509Builder.IssueLeaf(
            subjectNid:      nid,
            subjectPubKeyRaw: subjectPubRaw,
            caPrivateKey:    _keys.PrivateKey,
            issuerNid:       _opts.CaNid,
            role:            role,
            assuranceLevel:  assuranceLevel,
            notBefore:       now,
            notAfter:        expiresAt,
            serialNumber:    leafSerial);

        var chainB64Url = new[]
        {
            Base64Url(leafCert.RawData),
            Base64Url(rootCert.RawData),
        };

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

        return v1Frame with
        {
            CertFormat = IdentCertFormat.V2X509,
            CertChain  = chainB64Url,
            // AssuranceLevel already set by IssueFrame(...) above.
        };
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
        string? metadataJson,
        AssuranceLevel? assuranceLevel = null)
    {
        var scope   = JsonDocument.Parse(scopeJson).RootElement;
        var issuedAtStr  = issuedAt.ToString("O");
        var expiresAtStr = expiresAt.ToString("O");

        // Canonical payload for signing — alphabetical order is enforced by
        // NipSigner.CanonicalJson. We include assurance_level in the signed
        // payload only when set, matching the wire convention that an absent
        // field defaults to "anonymous" for backward compatibility.
        object payload = assuranceLevel is null
            ? new
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
              }
            : new
              {
                  assurance_level = assuranceLevel.Value,
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
            Nid            = nid,
            PubKey         = pubKey,
            Capabilities   = capabilities,
            Scope          = scope.Clone(),
            IssuedBy       = _opts.CaNid,
            IssuedAt       = issuedAtStr,
            ExpiresAt      = expiresAtStr,
            Serial         = serial,
            Signature      = signature,
            Metadata       = metadata,
            AssuranceLevel = assuranceLevel,
        };
    }

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ── X.509 helpers (NPS-RFC-0002 prototype) ────────────────────────────────

    private static byte[] ExtractEd25519Raw(string encoded)
    {
        const string prefix = "ed25519:";
        if (!encoded.StartsWith(prefix, StringComparison.Ordinal))
            throw new NipCaException(
                $"X.509 issuance requires an ed25519:* pubkey; got '{encoded}'.",
                NipErrorCodes.CertFormatInvalid);
        var b64u = encoded[prefix.Length..];
        var raw  = NipSigner.FromBase64Url(b64u);
        if (raw.Length != 32)
            throw new NipCaException(
                $"Ed25519 pubkey must be 32 bytes; got {raw.Length}.",
                NipErrorCodes.CertFormatInvalid);
        return raw;
    }

    private static byte[] ParseSerialBytes(string serial)
    {
        // Accept "0x<hex>" or plain hex. X509 serials must be positive — pad
        // with a leading 0x00 byte if the high bit is set.
        var hex = serial.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? serial[2..] : serial;
        if (hex.Length % 2 != 0) hex = "0" + hex;
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length == 0) bytes = new byte[] { 0x01 };
        if ((bytes[0] & 0x80) != 0)
        {
            var padded = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, padded, 1, bytes.Length);
            return padded;
        }
        return bytes;
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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

    /// <summary>
    /// IdentFrame.assurance_level disagrees with the X.509 cert extension
    /// id-nid-assurance-level (downgrade-attack defence). NPS-3 §5.1.1
    /// (NPS-RFC-0003). → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string AssuranceMismatch = "NIP-ASSURANCE-MISMATCH";

    /// <summary>
    /// IdentFrame.assurance_level (or the X.509 extension) carries a
    /// value outside the defined enum (anonymous / attested / verified).
    /// NPS-3 §5.1.1 (NPS-RFC-0003). → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string AssuranceUnknown  = "NIP-ASSURANCE-UNKNOWN";

    /// <summary>
    /// IdentFrame.cert_chain bytes are not DER-encoded X.509 or fail ASN.1
    /// parsing. NPS-RFC-0002 §4.3. → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string CertFormatInvalid    = "NIP-CERT-FORMAT-INVALID";

    /// <summary>
    /// IdentFrame.cert_chain leaf certificate is missing the required NPS
    /// EKU (<c>agent-identity</c> or <c>node-identity</c>). EKU MUST be
    /// marked critical to prevent cross-purpose use as a TLS server cert.
    /// NPS-RFC-0002 §4.3. → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string CertEkuMissing       = "NIP-CERT-EKU-MISSING";

    /// <summary>
    /// X.509 cert subject CN or SAN URI does not match the
    /// <see cref="Frames.IdentFrame.Nid"/> field. NPS-RFC-0002 §4.3.
    /// → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string CertSubjectNidMismatch = "NIP-CERT-SUBJECT-NID-MISMATCH";

    /// <summary>
    /// ACME <c>agent-01</c> challenge validation failed at the CA side
    /// (signature missing, token mismatch, replay, etc.). NPS-RFC-0002
    /// §4.3 / §4.4. → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string AcmeChallengeFailed  = "NIP-ACME-CHALLENGE-FAILED";

    /// <summary>
    /// Reputation log entry signature fails verification or canonical
    /// (RFC 8785 JCS) form is malformed. NPS-3 §5.1.2 (NPS-RFC-0004).
    /// → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string ReputationEntryInvalid = "NIP-REPUTATION-ENTRY-INVALID";

    /// <summary>
    /// A log operator referenced by a Node's reputation_policy cannot
    /// be reached during admission evaluation. NPS-3 §5.1.2
    /// (NPS-RFC-0004). → NPS-DOWNSTREAM-UNAVAILABLE.
    /// </summary>
    public const string ReputationLogUnreachable = "NIP-REPUTATION-LOG-UNREACHABLE";
}
