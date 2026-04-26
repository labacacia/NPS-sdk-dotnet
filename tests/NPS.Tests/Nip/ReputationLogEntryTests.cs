// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core;
using NPS.NIP.Ca;
using NPS.NIP.Crypto;
using NPS.NIP.Reputation;
using NPS.NWP.Http;
using NSec.Cryptography;

namespace NPS.Tests.Nip;

/// <summary>
/// Phase 1 reference tests for NPS-RFC-0004 (CT-style NID reputation
/// log). These exercise the entry record + JCS sign/verify wrapper
/// around <see cref="NipSigner"/>; Merkle tree, STH, inclusion proofs,
/// and the operator HTTP API land in Phase 2 and have separate tests.
/// </summary>
public sealed class ReputationLogEntryTests
{
    private static readonly JsonSerializerOptions WireOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Severity enum + JSON converter ─────────────────────────────────────

    [Fact]
    public void Severity_IsOrdered_InfoMinorModerateMajorCritical()
    {
        Assert.True(Severity.Info     < Severity.Minor);
        Assert.True(Severity.Minor    < Severity.Moderate);
        Assert.True(Severity.Moderate < Severity.Major);
        Assert.True(Severity.Major    < Severity.Critical);
    }

    [Theory]
    [InlineData(Severity.Info,     "info")]
    [InlineData(Severity.Minor,    "minor")]
    [InlineData(Severity.Moderate, "moderate")]
    [InlineData(Severity.Major,    "major")]
    [InlineData(Severity.Critical, "critical")]
    public void Severity_RoundTripsThroughWireString(Severity sev, string wire)
    {
        Assert.Equal(wire, Severities.ToWire(sev));
        Assert.True(Severities.TryParse(wire, out var parsed));
        Assert.Equal(sev, parsed);
    }

    [Theory]
    [InlineData("Major")]   // wrong case
    [InlineData("urgent")]  // not in vocabulary
    [InlineData("")]
    public void Severity_TryParse_RejectsUnknown(string wire)
    {
        Assert.False(Severities.TryParse(wire, out _));
    }

    [Fact]
    public void SeverityJsonConverter_ThrowsOnUnknownString()
    {
        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<Severity>("\"urgent\""));
        Assert.Contains("NIP-REPUTATION-ENTRY-INVALID", ex.Message);
    }

    // ── IncidentType enum + JSON converter ─────────────────────────────────

    [Theory]
    [InlineData(IncidentType.CertRevoked,         "cert-revoked")]
    [InlineData(IncidentType.RateLimitViolation,  "rate-limit-violation")]
    [InlineData(IncidentType.TosViolation,        "tos-violation")]
    [InlineData(IncidentType.ScrapingPattern,     "scraping-pattern")]
    [InlineData(IncidentType.PaymentDefault,      "payment-default")]
    [InlineData(IncidentType.ContractDispute,     "contract-dispute")]
    [InlineData(IncidentType.ImpersonationClaim,  "impersonation-claim")]
    [InlineData(IncidentType.PositiveAttestation, "positive-attestation")]
    public void IncidentType_RoundTripsThroughWireString(IncidentType kind, string wire)
    {
        Assert.Equal(wire, IncidentTypes.ToWire(kind));
        Assert.True(IncidentTypes.TryParse(wire, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Fact]
    public void IncidentType_TryParse_UnknownYieldsOther()
    {
        Assert.False(IncidentTypes.TryParse("future-incident", out var kind));
        Assert.Equal(IncidentType.Other, kind);
    }

    [Fact]
    public void IncidentType_ToWire_ThrowsOnOtherSentinel()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => IncidentTypes.ToWire(IncidentType.Other));
        Assert.Contains("IncidentRaw", ex.Message);
    }

    [Fact]
    public void IncidentTypeJsonConverter_UnknownStringDecodesAsOther()
    {
        // Per §5.1.2.1 receivers MUST preserve unknown values as
        // forward-compat pass-through; the converter returns Other
        // and callers consult IncidentRaw on the parent record.
        var got = JsonSerializer.Deserialize<IncidentType>("\"future-incident\"");
        Assert.Equal(IncidentType.Other, got);
    }

    [Fact]
    public void IncidentTypeJsonConverter_NonStringTokenThrows()
    {
        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<IncidentType>("42"));
        Assert.Contains("NIP-REPUTATION-ENTRY-INVALID", ex.Message);
    }

    [Fact]
    public void IncidentTypeJsonConverter_WriteOtherThrows()
    {
        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Serialize(IncidentType.Other));
        Assert.Contains("IncidentRaw", ex.Message);
    }

    // ── ReputationLogEntry round-trip ───────────────────────────────────────

    private static ReputationLogEntry MakeEntry(string sigPlaceholder = "ed25519:placeholder")
        => new()
        {
            Version     = 1,
            LogId       = "urn:nps:node:log.example.com:primary",
            Seq         = 42817,
            Timestamp   = "2026-04-26T14:30:00Z",
            SubjectNid  = "urn:nps:agent:ca.example.com:test-001",
            Incident    = IncidentType.RateLimitViolation,
            Severity    = Severity.Moderate,
            Window      = new ObservationWindow("2026-04-26T13:00:00Z", "2026-04-26T14:00:00Z"),
            Observation = JsonDocument.Parse("""{"requests":45000,"threshold":300}""").RootElement,
            EvidenceRef = "https://log.example.com/evidence/42817",
            EvidenceSha256 = "abc123def456",
            IssuerNid   = "urn:nps:node:gateway.example.com:north-1",
            Signature   = sigPlaceholder,
        };

    [Fact]
    public void Entry_SerialisesAllFieldsToWireForm()
    {
        var entry = MakeEntry();
        var json  = JsonSerializer.Serialize(entry, WireOpts);

        Assert.Contains("\"v\":1", json);
        Assert.Contains("\"log_id\":", json);
        Assert.Contains("\"seq\":42817", json);
        Assert.Contains("\"subject_nid\":", json);
        Assert.Contains("\"incident\":\"rate-limit-violation\"", json);
        Assert.Contains("\"severity\":\"moderate\"", json);
        Assert.Contains("\"issuer_nid\":", json);
        Assert.Contains("\"signature\":", json);
    }

    [Fact]
    public void Entry_DeserialisesKnownIncident()
    {
        const string json = """
        {
          "v": 1,
          "log_id": "urn:nps:node:log.example.com:primary",
          "seq": 1,
          "timestamp": "2026-04-26T00:00:00Z",
          "subject_nid": "urn:nps:agent:x:1",
          "incident": "scraping-pattern",
          "severity": "major",
          "issuer_nid": "urn:nps:node:y:1",
          "signature": "ed25519:abc"
        }
        """;
        var entry = JsonSerializer.Deserialize<ReputationLogEntry>(json, WireOpts);
        Assert.NotNull(entry);
        Assert.Equal(IncidentType.ScrapingPattern, entry!.Incident);
        Assert.Equal(Severity.Major, entry.Severity);
    }

    [Fact]
    public void Entry_PreservesUnknownIncidentAsOther_ForwardCompat()
    {
        // Per §5.1.2.1 the wire format is forward-compatible:
        // unknown incident values must round-trip without crashing.
        const string json = """
        {
          "v": 1,
          "log_id": "x",
          "seq": 1,
          "timestamp": "2026-04-26T00:00:00Z",
          "subject_nid": "x",
          "incident": "future-quantum-attack-claim",
          "severity": "info",
          "issuer_nid": "x",
          "signature": "ed25519:x"
        }
        """;
        var entry = JsonSerializer.Deserialize<ReputationLogEntry>(json, WireOpts);
        Assert.NotNull(entry);
        Assert.Equal(IncidentType.Other, entry!.Incident);
    }

    // ── Sign / verify pipeline ──────────────────────────────────────────────

    private static (Key Private, PublicKey Public) NewEd25519KeyPair()
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (key, key.PublicKey);
    }

    [Fact]
    public void Sign_PopulatesSignature_AndVerifyAccepts()
    {
        var (priv, pub) = NewEd25519KeyPair();
        var unsigned    = MakeEntry(sigPlaceholder: ""); // placeholder; signer overwrites
        var signed      = ReputationLogEntrySigner.Sign(priv, unsigned);

        Assert.StartsWith("ed25519:", signed.Signature);
        Assert.True(ReputationLogEntrySigner.Verify(pub, signed));
    }

    [Fact]
    public void Verify_RejectsTamperedSubject()
    {
        var (priv, pub) = NewEd25519KeyPair();
        var signed      = ReputationLogEntrySigner.Sign(priv, MakeEntry(""));
        var tampered    = signed with { SubjectNid = "urn:nps:agent:evil:1" };

        Assert.False(ReputationLogEntrySigner.Verify(pub, tampered));
    }

    [Fact]
    public void Verify_RejectsTamperedSeverity()
    {
        var (priv, pub) = NewEd25519KeyPair();
        var signed      = ReputationLogEntrySigner.Sign(priv, MakeEntry(""));
        // Bump severity — re-signing required, so verify must fail.
        var tampered    = signed with { Severity = Severity.Critical };

        Assert.False(ReputationLogEntrySigner.Verify(pub, tampered));
    }

    [Fact]
    public void Verify_RejectsForeignKey()
    {
        var (issuerPriv, _)    = NewEd25519KeyPair();
        var (_, attackerPub)   = NewEd25519KeyPair();
        var signed             = ReputationLogEntrySigner.Sign(issuerPriv, MakeEntry(""));

        Assert.False(ReputationLogEntrySigner.Verify(attackerPub, signed));
    }

    [Fact]
    public void Sign_NullArgumentsThrow()
    {
        var (priv, _) = NewEd25519KeyPair();
        Assert.Throws<ArgumentNullException>(() => ReputationLogEntrySigner.Sign(null!, MakeEntry()));
        Assert.Throws<ArgumentNullException>(() => ReputationLogEntrySigner.Sign(priv, null!));
    }

    // ── Error / status code constants ───────────────────────────────────────

    [Fact]
    public void ErrorCodeConstants_MatchSpec()
    {
        Assert.Equal("NIP-REPUTATION-ENTRY-INVALID",  NipErrorCodes.ReputationEntryInvalid);
        Assert.Equal("NIP-REPUTATION-LOG-UNREACHABLE", NipErrorCodes.ReputationLogUnreachable);
        Assert.Equal("NWP-AUTH-REPUTATION-BLOCKED",   NwpErrorCodes.AuthReputationBlocked);
        Assert.Equal("NPS-DOWNSTREAM-UNAVAILABLE",     NpsStatusCodes.DownstreamUnavailable);
    }

    [Fact]
    public void WireStringConstants_MatchSpec()
    {
        // Defends against accidental rename — these strings are wire-visible
        // and MUST stay aligned with NPS-3 §5.1.2 / §5.1.2.1.
        Assert.Equal("info",     Severities.InfoWire);
        Assert.Equal("minor",    Severities.MinorWire);
        Assert.Equal("moderate", Severities.ModerateWire);
        Assert.Equal("major",    Severities.MajorWire);
        Assert.Equal("critical", Severities.CriticalWire);

        Assert.Equal("cert-revoked",         IncidentTypes.CertRevokedWire);
        Assert.Equal("rate-limit-violation", IncidentTypes.RateLimitViolationWire);
        Assert.Equal("tos-violation",        IncidentTypes.TosViolationWire);
        Assert.Equal("scraping-pattern",     IncidentTypes.ScrapingPatternWire);
        Assert.Equal("payment-default",      IncidentTypes.PaymentDefaultWire);
        Assert.Equal("contract-dispute",     IncidentTypes.ContractDisputeWire);
        Assert.Equal("impersonation-claim",  IncidentTypes.ImpersonationClaimWire);
        Assert.Equal("positive-attestation", IncidentTypes.PositiveAttestationWire);
    }
}
