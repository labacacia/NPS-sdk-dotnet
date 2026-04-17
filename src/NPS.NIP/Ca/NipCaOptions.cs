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

    /// <summary>PostgreSQL connection string for certificate storage.</summary>
    public required string ConnectionString { get; set; }

    // ── Security ──────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, OCSP responses are delayed to a minimum of 200 ms to prevent
    /// timing-based certificate status inference (NPS-3 §10.2).
    /// Default true.
    /// </summary>
    public bool NormalizeOcspResponseTime { get; set; } = true;

    /// <summary>Supported algorithms advertised in the well-known response. Default ["ed25519"].</summary>
    public IReadOnlyList<string> Algorithms { get; set; } = ["ed25519"];
}
