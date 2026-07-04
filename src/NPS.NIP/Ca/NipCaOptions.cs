// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Ca;

/// <summary>
/// Configuration for the NIP CA Server (NPS-3 §8).
/// Register via <c>AddNipCa()</c> DI extension.
/// </summary>
public sealed class NipCaOptions
{
    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>
    /// CA NID, e.g. <c>urn:nps:org:ca.example.com</c>.
    /// Used as <c>issued_by</c> in all issued IdentFrames.
    /// </summary>
    public required string CaNid { get; set; }

    /// <summary>Human-readable CA name shown in the <c>/.well-known/nps-ca</c> response.</summary>
    public string? DisplayName { get; set; }

    // ── Key material ──────────────────────────────────────────────────────────

    /// <summary>
    /// Path to the AES-256-GCM encrypted CA private key file.
    /// Generate via <c>nip-ca keygen</c> or <c>NipKeyManager.Generate()</c>.
    /// </summary>
    public required string KeyFilePath { get; set; }

    /// <summary>
    /// Passphrase used to decrypt <see cref="KeyFilePath"/>.
    /// MUST be supplied via environment variable — never hardcoded.
    /// </summary>
    public required string KeyPassphrase { get; set; }

    // ── Certificate lifetimes ─────────────────────────────────────────────────

    /// <summary>Agent certificate validity in days. Default 30 (NPS-3 §2.2).</summary>
    public int AgentCertValidityDays { get; set; } = 30;

    /// <summary>Node certificate validity in days. Default 90 (NPS-3 §2.2).</summary>
    public int NodeCertValidityDays { get; set; } = 90;

    /// <summary>Renewal window in days before expiry. Default 7 (NPS-3 §6).</summary>
    public int RenewalWindowDays { get; set; } = 7;

    /// <summary>
    /// Orchestrator group NID validity in days. Default 365.
    /// Group NIDs are longer-lived than agent NIDs because they are the
    /// trust anchor for many short-lived sessions (NPS-CR-0003 §5.1.3).
    /// </summary>
    public int GroupCertValidityDays { get; set; } = 365;

    /// <summary>
    /// Default validity for session NIDs when the issue request does not
    /// specify <c>validity_seconds</c>. Default 1 hour (NPS-CR-0003 §5.1.3).
    /// </summary>
    public TimeSpan SessionDefaultValidity { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum permitted session validity. Requests above this are rejected
    /// with <c>NIP-CA-SESSION-VALIDITY-INVALID</c>. Default 24 hours
    /// (NPS-CR-0003 §5.1.3).
    /// </summary>
    public TimeSpan SessionMaxValidity { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Minimum permitted session validity. Default 60 seconds. Below this is
    /// rejected with <c>NIP-CA-SESSION-VALIDITY-INVALID</c> — too short to be
    /// usable in practice.
    /// </summary>
    public TimeSpan SessionMinValidity { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Allowed clock-skew window for the group-JWS <c>iat</c> on a
    /// session-issue request. Default ±5 minutes. Outside this window the
    /// CA returns <c>NIP-CA-JWS-EXPIRED</c> (NPS-CR-0003 §5.1.3 / §3.5).
    /// </summary>
    public TimeSpan SessionJwsClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    // ── Exposure ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Base URL of this CA server, e.g. <c>https://ca.example.com</c>.
    /// Used to build endpoint URLs in the <c>/.well-known/nps-ca</c> response.
    /// </summary>
    public required string BaseUrl { get; set; }

    /// <summary>
    /// HTTP route prefix for CA endpoints. Default <c>""</c> (mounted at root).
    /// Set to e.g. <c>"/nip"</c> when embedding in a larger app.
    /// </summary>
    public string RoutePrefix { get; set; } = "";

    // ── Database ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Connection string for certificate storage.
    /// Required when using the PostgreSQL backend via <c>AddNipCaWithPostgres()</c>.
    /// Not needed when supplying a custom <c>INipCaStore</c> via
    /// <c>AddNipCa(..., INipCaStore)</c> or <c>AddNipCaWithSqlite()</c>.
    /// </summary>
    public string? ConnectionString { get; set; }

    // ── Security ──────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, OCSP responses are delayed to a minimum of 200 ms to prevent
    /// timing-based certificate status inference (NPS-3 §10.2).
    /// Default true.
    /// </summary>
    public bool NormalizeOcspResponseTime { get; set; } = true;

    /// <summary>Supported algorithms advertised in the well-known response. Default ["ed25519"].</summary>
    public IReadOnlyList<string> Algorithms { get; set; } = ["ed25519"];

    // ── Auth ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bearer token required on operator endpoints (register, revoke, renew).
    /// Set via env NIPCA__OPERATORAPIKEY.
    /// When null, operator auth is skipped (development only — not for production).
    /// </summary>
    public string? OperatorApiKey { get; set; }

    /// <summary>
    /// When non-null, only capabilities in this set may be requested at registration.
    /// Requests with unlisted capabilities are rejected with 403.
    /// </summary>
    public IReadOnlySet<string>? AllowedCapabilities { get; set; }

    // ── Enrollment / RA (NPS-CR-0005) ────────────────────────────────────────

    /// <summary>
    /// Enrollment tier that governs how inbound registration requests are
    /// admitted (NPS-CR-0005 §3). Default <see cref="EnrollmentTier.Allowlist"/>.
    /// </summary>
    public EnrollmentTier EnrollmentTier { get; set; } = EnrollmentTier.Allowlist;

    /// <summary>
    /// Glob patterns used by <see cref="EnrollmentTier.Allowlist"/> (Tier 1).
    /// An identifier that matches at least one pattern is admitted; all
    /// others are rejected with <c>NIP-RA-NID-NOT-ALLOWED</c>.
    /// Default <c>["*"]</c> (open CA — all identifiers allowed).
    /// </summary>
    public IReadOnlyList<string> EnrollmentAllowlistPatterns { get; set; } = ["*"];

    /// <summary>
    /// Maximum TTL for bootstrap tokens created via
    /// <c>POST /v1/enrollment/tokens</c>. Requests that exceed this cap are
    /// clamped. Default 24 hours (NPS-CR-0005 §3.3).
    /// </summary>
    public TimeSpan BootstrapTokenMaxTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum number of records in <c>Pending</c> status allowed in the
    /// queue at one time (NPS-CR-0005 §3.4). New registrations are rejected
    /// with <c>503</c> when the queue is full. Default 1000.
    /// </summary>
    public int PendingQueueMaxSize { get; set; } = 1000;

    /// <summary>
    /// Age after which non-pending (approved/rejected) records are swept from
    /// the in-memory pending store. Default 7 days (NPS-CR-0005 §3.4).
    /// </summary>
    public TimeSpan PendingQueueMaxAge { get; set; } = TimeSpan.FromDays(7);

    // ── ACME ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables the ACME server (RFC 8555 + NPS-RFC-0002 <c>agent-01</c> challenge).
    /// Set via env NIPCA__ACMEENABLED=true.
    /// When false (default), no ACME middleware is mounted.
    /// </summary>
    public bool AcmeEnabled { get; set; } = false;

    /// <summary>HTTP path prefix for ACME endpoints. Default <c>"/acme"</c>.</summary>
    public string AcmePathPrefix { get; set; } = "/acme";
}
