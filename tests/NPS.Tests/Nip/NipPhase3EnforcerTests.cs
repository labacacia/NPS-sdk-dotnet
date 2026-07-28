using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NPS.NIP.Ca;
using NPS.NIP.Frames;
using NPS.NIP.Verification;
using NPS.NIP.X509;
using Xunit;

namespace NPS.Tests.Nip;

/// <summary>NIP v0.11 §7.5 Phase-3 enforcement: subset checks + OCSP-staple freshness.</summary>
public sealed class NipPhase3EnforcerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    // ── helpers ───────────────────────────────────────────────────────────────

    private static byte[] Utf8Seq(params string[] values)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
            foreach (var v in values)
                w.WriteCharacterString(UniversalTagNumber.UTF8String, v);
        return w.Encode();
    }

    /// <summary>Self-signed ECDSA test cert, optionally carrying id-nps-* extensions.</summary>
    private static X509Certificate2 Cert(string[]? roles = null, string[]? caps = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=phase3-test", key, HashAlgorithmName.SHA256);
        if (roles is not null)
            req.CertificateExtensions.Add(new X509Extension(NpsX509Oids.IdNpsNodeRoles, Utf8Seq(roles), critical: false));
        if (caps is not null)
            req.CertificateExtensions.Add(new X509Extension(NpsX509Oids.IdNpsCapabilities, Utf8Seq(caps), critical: false));
        return req.CreateSelfSigned(Now.AddDays(-1), Now.AddDays(30));
    }

    /// <summary>Minimal RFC 6960 OCSPResponse DER whose first SingleResponse.nextUpdate = <paramref name="nextUpdate"/>.</summary>
    private static string Staple(DateTimeOffset nextUpdate)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())                                   // OCSPResponse
        {
            w.WriteEnumeratedValue((System.Security.Cryptography.X509Certificates.X509RevocationReason)0); // responseStatus successful(0)
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) // responseBytes [0]
            using (w.PushSequence())                               // ResponseBytes
            {
                w.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");   // id-pkix-ocsp-basic
                // response OCTET STRING wrapping BasicOCSPResponse
                var inner = new AsnWriter(AsnEncodingRules.DER);
                using (inner.PushSequence())                       // BasicOCSPResponse
                using (inner.PushSequence())                       // tbsResponseData (first SEQUENCE)
                {
                    // responderID [2] byKey (context-specific, primitive-wrapped octet string)
                    using (inner.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                        inner.WriteOctetString(new byte[20]);
                    inner.WriteGeneralizedTime(Now.AddMinutes(-5)); // producedAt
                    using (inner.PushSequence())                    // responses SEQUENCE OF
                    using (inner.PushSequence())                    // SingleResponse
                    {
                        using (inner.PushSequence())                // certID
                        {
                            inner.WriteOctetString([1]);
                        }
                        inner.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 0)); // certStatus good [0] IMPLICIT NULL
                        inner.WriteGeneralizedTime(Now.AddMinutes(-5));            // thisUpdate
                        using (inner.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) // nextUpdate [0] EXPLICIT
                            inner.WriteGeneralizedTime(nextUpdate);
                    }
                }
                w.WriteOctetString(inner.Encode());
            }
        }
        return Convert.ToBase64String(w.Encode()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static IdentFrame Frame(string[]? nodeRoles = null, string[]? caps = null, string? staple = null) => new()
    {
        Nid          = "urn:nps:agent:ca.example.com:p3-001",
        PubKey       = "ed25519:AAAA",
        Capabilities = caps ?? ["nwp:query"],
        NodeRoles    = nodeRoles,
        Scope        = JsonDocument.Parse("""{ "nodes": ["nwp://example.com/*"] }""").RootElement,
        IssuedBy     = "urn:nps:org:example.com",
        IssuedAt     = "2026-07-01T00:00:00Z",
        ExpiresAt    = "2026-08-01T00:00:00Z",
        Serial       = "0x01",
        Signature    = "ed25519:test",
        OcspStaple   = staple,
    };

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Subset_claims_with_fresh_staple_pass()
    {
        using var cert = Cert(roles: ["memory", "anchor"], caps: ["nwp:query", "nwp:action"]);
        var r = NipPhase3Enforcer.Enforce(
            Frame(nodeRoles: ["memory"], caps: ["nwp:query"], staple: Staple(Now.AddHours(6))), cert, Now);
        Assert.True(r.IsValid, r.Message);
    }

    [Fact]
    public void Unattested_role_fails_with_node_roles_mismatch()
    {
        using var cert = Cert(roles: ["memory"]);
        var r = NipPhase3Enforcer.Enforce(
            Frame(nodeRoles: ["memory", "orchestrator"], staple: Staple(Now.AddHours(6))), cert, Now);
        Assert.False(r.IsValid);
        Assert.Equal(NipErrorCodes.CertNodeRolesMismatch, r.ErrorCode);
    }

    [Fact]
    public void Unattested_capability_fails_with_capabilities_exceeded()
    {
        using var cert = Cert(caps: ["nwp:query"]);
        var r = NipPhase3Enforcer.Enforce(
            Frame(caps: ["nwp:query", "nop:orchestrate"], staple: Staple(Now.AddHours(6))), cert, Now);
        Assert.False(r.IsValid);
        Assert.Equal(NipErrorCodes.CertCapabilitiesExceeded, r.ErrorCode);
    }

    [Fact]
    public void No_extensions_means_attribute_checks_do_not_apply()
    {
        using var cert = Cert(); // no id-nps-* extensions
        var r = NipPhase3Enforcer.Enforce(
            Frame(nodeRoles: ["anything"], caps: ["anything:else"], staple: Staple(Now.AddHours(6))), cert, Now);
        Assert.True(r.IsValid, r.Message);
    }

    [Fact]
    public void Missing_staple_fails()
    {
        using var cert = Cert();
        var r = NipPhase3Enforcer.Enforce(Frame(staple: null), cert, Now);
        Assert.False(r.IsValid);
        Assert.Equal(NipErrorCodes.OcspStapleExpired, r.ErrorCode);
    }

    [Fact]
    public void Expired_staple_fails()
    {
        using var cert = Cert();
        var r = NipPhase3Enforcer.Enforce(Frame(staple: Staple(Now.AddMinutes(-1))), cert, Now);
        Assert.False(r.IsValid);
        Assert.Equal(NipErrorCodes.OcspStapleExpired, r.ErrorCode);
        Assert.Contains("elapsed", r.Message);
    }

    [Fact]
    public void Malformed_staple_fails_closed()
    {
        using var cert = Cert();
        var r = NipPhase3Enforcer.Enforce(Frame(staple: "bm90LWFuLW9jc3A"), cert, Now); // "not-an-ocsp"
        Assert.False(r.IsValid);
        Assert.Equal(NipErrorCodes.OcspStapleExpired, r.ErrorCode);
    }

    [Fact]
    public void Utf8_sequence_extension_parses()
    {
        using var cert = Cert(roles: ["memory", "anchor"]);
        var parsed = NipPhase3Enforcer.ReadUtf8SequenceExtension(cert, NpsX509Oids.IdNpsNodeRoles);
        Assert.Equal(new[] { "memory", "anchor" }, parsed);
        Assert.Null(NipPhase3Enforcer.ReadUtf8SequenceExtension(cert, NpsX509Oids.IdNpsCapabilities));
    }
}
