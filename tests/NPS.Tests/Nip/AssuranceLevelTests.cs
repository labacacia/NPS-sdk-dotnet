// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NIP;
using NPS.NIP.Ca;
using NPS.NIP.Frames;
using NPS.NIP.Verification;
using NPS.NWP.Http;
using NPS.NWP.Nwm;

namespace NPS.Tests.Nip;

/// <summary>
/// Phase 1 reference tests for NPS-RFC-0003 (Three-tier Agent identity
/// assurance levels). These exercise the enum, wire conversion, frame /
/// manifest round-trip, and the per-request <see cref="NipVerifyContext"/>
/// extension. Active enforcement in <c>NipIdentVerifier</c> is opt-in
/// per RFC §8.1 and is tested when Phase 2 ships.
/// </summary>
public sealed class AssuranceLevelTests
{
    /// <summary>
    /// Mirrors the project-wide wire convention (snake_case on the wire,
    /// see <c>NPS.Core.Codecs.Tier1JsonCodec</c>). Required for
    /// <c>required</c> property matching in System.Text.Json.
    /// </summary>
    private static readonly JsonSerializerOptions _wireOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void Enum_IsOrdered_AnonymousLessThanAttestedLessThanVerified()
    {
        Assert.True(AssuranceLevel.Anonymous < AssuranceLevel.Attested);
        Assert.True(AssuranceLevel.Attested  < AssuranceLevel.Verified);
        Assert.Equal(0, (int)AssuranceLevel.Anonymous);
        Assert.Equal(1, (int)AssuranceLevel.Attested);
        Assert.Equal(2, (int)AssuranceLevel.Verified);
    }

    [Theory]
    [InlineData(AssuranceLevel.Anonymous, "anonymous")]
    [InlineData(AssuranceLevel.Attested,  "attested")]
    [InlineData(AssuranceLevel.Verified,  "verified")]
    public void ToWire_ProducesSpecString(AssuranceLevel level, string expected)
    {
        Assert.Equal(expected, AssuranceLevels.ToWire(level));
    }

    [Theory]
    [InlineData("anonymous", AssuranceLevel.Anonymous)]
    [InlineData("attested",  AssuranceLevel.Attested)]
    [InlineData("verified",  AssuranceLevel.Verified)]
    public void TryParse_AcceptsKnownWireStrings(string wire, AssuranceLevel expected)
    {
        Assert.True(AssuranceLevels.TryParse(wire, out var got));
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Anonymous")]   // wrong case — wire is lowercase
    [InlineData("attested ")]   // trailing whitespace
    [InlineData("super-verified")]
    public void TryParse_RejectsUnknownOrEmpty(string? wire)
    {
        Assert.False(AssuranceLevels.TryParse(wire, out var got));
        Assert.Equal(AssuranceLevel.Anonymous, got); // out value is reset to anonymous
    }

    [Fact]
    public void FromWireOrAnonymous_TreatsNullAndUnknownAsAnonymous()
    {
        // Per NPS-3 §5.1.1: absent → anonymous (backward compat with
        // pre-RFC-0003 publishers). Receivers MUST reject *known-bad*
        // strings (NIP-ASSURANCE-UNKNOWN) but a missing field is fine.
        Assert.Equal(AssuranceLevel.Anonymous, AssuranceLevels.FromWireOrAnonymous(null));
        Assert.Equal(AssuranceLevel.Anonymous, AssuranceLevels.FromWireOrAnonymous(""));
        Assert.Equal(AssuranceLevel.Anonymous, AssuranceLevels.FromWireOrAnonymous("xyz"));
        Assert.Equal(AssuranceLevel.Verified,  AssuranceLevels.FromWireOrAnonymous("verified"));
    }

    [Fact]
    public void ToWire_ThrowsOnUndefinedEnumValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AssuranceLevels.ToWire((AssuranceLevel)42));
    }

    // ── JSON converter ─────────────────────────────────────────────────────

    [Fact]
    public void JsonConverter_RoundTripsAllThreeLevels()
    {
        var opts = new JsonSerializerOptions();
        foreach (var level in new[] { AssuranceLevel.Anonymous, AssuranceLevel.Attested, AssuranceLevel.Verified })
        {
            var json    = JsonSerializer.Serialize(level, opts);
            var roundTrip = JsonSerializer.Deserialize<AssuranceLevel>(json, opts);
            Assert.Equal(level, roundTrip);
            Assert.Contains(AssuranceLevels.ToWire(level), json);
        }
    }

    [Fact]
    public void JsonConverter_ReadsNullAsAnonymous_ForBackwardCompat()
    {
        var got = JsonSerializer.Deserialize<AssuranceLevel>("null");
        Assert.Equal(AssuranceLevel.Anonymous, got);
    }

    [Fact]
    public void JsonConverter_ThrowsOnUnknownString_MapsToAssuranceUnknown()
    {
        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<AssuranceLevel>("\"super-attested\""));
        Assert.Contains("NIP-ASSURANCE-UNKNOWN", ex.Message);
    }

    [Fact]
    public void JsonConverter_ThrowsOnNonStringToken_MapsToAssuranceUnknown()
    {
        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<AssuranceLevel>("2"));
        Assert.Contains("NIP-ASSURANCE-UNKNOWN", ex.Message);
    }

    // ── IdentFrame round-trip ──────────────────────────────────────────────

    private static IdentFrame MakeFrame(AssuranceLevel? level)
    {
        return new IdentFrame
        {
            Nid          = "urn:nps:agent:ca.example.com:test-001",
            PubKey       = "ed25519:MCowBQYDK2VwAyEA...",
            Capabilities = new[] { "nwp:query" },
            Scope        = JsonDocument.Parse("""{ "nodes": ["nwp://example.com/*"], "actions": [], "max_token_budget": 1000 }""").RootElement,
            IssuedBy     = "urn:nps:org:example.com",
            IssuedAt     = "2026-04-25T00:00:00Z",
            ExpiresAt    = "2026-05-25T00:00:00Z",
            Serial       = "0x01",
            Signature    = "ed25519:test",
            AssuranceLevel = level,
        };
    }

    [Fact]
    public void IdentFrame_SerialisesAssuranceLevel_AsLowercaseWireString()
    {
        var frame = MakeFrame(AssuranceLevel.Verified);
        var json  = JsonSerializer.Serialize(frame, _wireOpts);
        Assert.Contains("\"assurance_level\":\"verified\"", json);
    }

    [Fact]
    public void IdentFrame_OmitsAssuranceLevel_WhenNull()
    {
        var frame = MakeFrame(level: null);
        var json  = JsonSerializer.Serialize(frame, new JsonSerializerOptions(_wireOpts)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        Assert.DoesNotContain("assurance_level", json);
    }

    [Fact]
    public void IdentFrame_DeserialisesAssuranceLevel_FromWireString()
    {
        const string json = """
        {
          "frame": "0x20",
          "nid": "urn:nps:agent:ca.example.com:test-002",
          "pub_key": "ed25519:MCo",
          "capabilities": ["nwp:query"],
          "scope": { "nodes": [], "actions": [] },
          "issued_by": "urn:nps:org:example.com",
          "issued_at": "2026-04-25T00:00:00Z",
          "expires_at": "2026-05-25T00:00:00Z",
          "serial": "0x02",
          "signature": "ed25519:x",
          "assurance_level": "attested"
        }
        """;

        var frame = JsonSerializer.Deserialize<IdentFrame>(json, _wireOpts);
        Assert.NotNull(frame);
        Assert.Equal(AssuranceLevel.Attested, frame!.AssuranceLevel);
    }

    [Fact]
    public void IdentFrame_DeserialisesMissingAssuranceLevel_AsNull_WhichCallerNormalisesToAnonymous()
    {
        const string json = """
        {
          "frame": "0x20",
          "nid": "urn:nps:agent:ca.example.com:legacy",
          "pub_key": "ed25519:MCo",
          "capabilities": ["nwp:query"],
          "scope": { "nodes": [], "actions": [] },
          "issued_by": "urn:nps:org:example.com",
          "issued_at": "2026-04-25T00:00:00Z",
          "expires_at": "2026-05-25T00:00:00Z",
          "serial": "0x03",
          "signature": "ed25519:x"
        }
        """;

        var frame = JsonSerializer.Deserialize<IdentFrame>(json, _wireOpts);
        Assert.NotNull(frame);
        Assert.Null(frame!.AssuranceLevel);

        // Per §5.1.1: receivers normalise null → anonymous.
        var effective = frame.AssuranceLevel ?? AssuranceLevel.Anonymous;
        Assert.Equal(AssuranceLevel.Anonymous, effective);
    }

    // ── NWM round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void NeuralWebManifest_RoundTrips_MinAssuranceLevel()
    {
        const string json = """
        {
          "nwp": "0.6",
          "node_id": "urn:nps:node:api.example.com:orders",
          "node_type": "memory",
          "wire_formats": ["json"],
          "preferred_format": "json",
          "capabilities": { "query": true },
          "auth": { "required": false },
          "endpoints": { "manifest": "/.nwm" },
          "min_assurance_level": "attested"
        }
        """;

        var nwm = JsonSerializer.Deserialize<NeuralWebManifest>(json, _wireOpts);
        Assert.NotNull(nwm);
        Assert.Equal("attested", nwm!.MinAssuranceLevel);

        // Re-serialise and check that the field is preserved.
        var roundTrip = JsonSerializer.Serialize(nwm, _wireOpts);
        Assert.Contains("\"min_assurance_level\":\"attested\"", roundTrip);
    }

    [Fact]
    public void NeuralWebManifest_OmitsMinAssuranceLevel_WhenNotSet()
    {
        const string json = """
        {
          "nwp": "0.6",
          "node_id": "urn:nps:node:api.example.com:orders",
          "node_type": "memory",
          "wire_formats": ["json"],
          "preferred_format": "json",
          "capabilities": { "query": true },
          "auth": { "required": false },
          "endpoints": { "manifest": "/.nwm" }
        }
        """;

        var nwm = JsonSerializer.Deserialize<NeuralWebManifest>(json, _wireOpts);
        Assert.NotNull(nwm);
        Assert.Null(nwm!.MinAssuranceLevel);
    }

    // ── NipVerifyContext + error-code constants ────────────────────────────

    [Fact]
    public void NipVerifyContext_CarriesMinAssuranceLevel()
    {
        var ctx = new NipVerifyContext
        {
            RequiredCapabilities = new[] { "nwp:query" },
            MinAssuranceLevel    = AssuranceLevel.Attested,
        };
        Assert.Equal(AssuranceLevel.Attested, ctx.MinAssuranceLevel);
    }

    [Fact]
    public void ErrorCodeConstants_MatchSpec()
    {
        // Defends against accidental rename — these strings are wire-visible
        // and MUST stay aligned with spec/error-codes.md.
        Assert.Equal("NIP-ASSURANCE-MISMATCH", NipErrorCodes.AssuranceMismatch);
        Assert.Equal("NIP-ASSURANCE-UNKNOWN",  NipErrorCodes.AssuranceUnknown);
        Assert.Equal("NWP-AUTH-ASSURANCE-TOO-LOW", NwpErrorCodes.AuthAssuranceTooLow);
    }

    [Fact]
    public void WireStringConstants_MatchSpec()
    {
        Assert.Equal("anonymous", AssuranceLevels.AnonymousWire);
        Assert.Equal("attested",  AssuranceLevels.AttestedWire);
        Assert.Equal("verified",  AssuranceLevels.VerifiedWire);
    }
}
