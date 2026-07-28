// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using NPS.NIP.Ca;
using NPS.NIP.Frames;
using NPS.NIP.X509;

namespace NPS.NIP.Verification;

/// <summary>
/// NIP v0.11 §7.5 Phase-3 enforcement: turns the Phase-1–2 advisory CA-attestation checks
/// into hard failures. Applies only to <c>v2-x509</c> frames; each attribute check applies
/// only when the corresponding certificate extension is present, so self-declared NIDs and
/// certs without attestation extensions are unaffected. Role/capability checks are
/// <b>subset</b> checks — the frame MUST NOT claim more than the CA attested.
/// </summary>
public static class NipPhase3Enforcer
{
    /// <summary>
    /// Run the Phase-3 checks against the leaf certificate. Returns <see cref="NipIdentVerifyResult.Ok"/>
    /// when every applicable check passes. <paramref name="now"/> is injectable for tests.
    /// </summary>
    public static NipIdentVerifyResult Enforce(IdentFrame frame, X509Certificate2 leaf, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(leaf);
        var when = now ?? DateTimeOffset.UtcNow;

        // 1. node_roles ⊆ id-nps-node-roles (only when the extension is present)
        var attestedRoles = ReadUtf8SequenceExtension(leaf, NpsX509Oids.IdNpsNodeRoles);
        if (attestedRoles is not null)
        {
            var claimed = frame.NodeRoles ?? Array.Empty<string>();
            var excess = claimed.Except(attestedRoles, StringComparer.Ordinal).ToList();
            if (excess.Count > 0)
            {
                return NipIdentVerifyResult.Fail(3,
                    NipErrorCodes.CertNodeRolesMismatch,
                    $"IdentFrame.node_roles claims role(s) not attested by id-nps-node-roles: {string.Join(", ", excess)}.");
            }
        }

        // 2. capabilities ⊆ id-nps-capabilities (only when the extension is present)
        var attestedCaps = ReadUtf8SequenceExtension(leaf, NpsX509Oids.IdNpsCapabilities);
        if (attestedCaps is not null)
        {
            var excess = frame.Capabilities.Except(attestedCaps, StringComparer.Ordinal).ToList();
            if (excess.Count > 0)
            {
                return NipIdentVerifyResult.Fail(3,
                    NipErrorCodes.CertCapabilitiesExceeded,
                    $"IdentFrame.capabilities claims capabilit(ies) not attested by id-nps-capabilities: {string.Join(", ", excess)}.");
            }
        }

        // 3. OCSP staple MUST be present and unexpired for v2-x509 under Phase 3.
        if (string.IsNullOrEmpty(frame.OcspStaple))
        {
            return NipIdentVerifyResult.Fail(3,
                NipErrorCodes.OcspStapleExpired,
                "Phase-3 enforcement requires ocsp_staple on v2-x509 IdentFrames; none was supplied.");
        }
        byte[] stapleDer;
        try
        {
            stapleDer = FromBase64Url(frame.OcspStaple);
        }
        catch (FormatException)
        {
            return NipIdentVerifyResult.Fail(3,
                NipErrorCodes.OcspStapleExpired,
                "ocsp_staple is not valid base64url.");
        }
        if (!TryGetOcspNextUpdate(stapleDer, out var nextUpdate))
        {
            return NipIdentVerifyResult.Fail(3,
                NipErrorCodes.OcspStapleExpired,
                "ocsp_staple could not be parsed as a DER OCSPResponse with a nextUpdate.");
        }
        if (nextUpdate <= when)
        {
            return NipIdentVerifyResult.Fail(3,
                NipErrorCodes.OcspStapleExpired,
                $"ocsp_staple nextUpdate {nextUpdate:o} has elapsed.");
        }

        return NipIdentVerifyResult.Ok();
    }

    /// <summary>
    /// Read an <c>SEQUENCE OF UTF8String</c> extension (id-nps-node-roles / id-nps-capabilities).
    /// Returns null when the certificate does not carry the extension; throws nothing — a
    /// malformed extension is treated as an empty attestation set (strictest interpretation:
    /// any claim then exceeds it).
    /// </summary>
    public static IReadOnlyList<string>? ReadUtf8SequenceExtension(X509Certificate2 cert, string oid)
    {
        var ext = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == oid);
        if (ext is null) return null;
        try
        {
            var reader = new AsnReader(ext.RawData, AsnEncodingRules.DER);
            var seq = reader.ReadSequence();
            var values = new List<string>();
            while (seq.HasData)
                values.Add(seq.ReadCharacterString(UniversalTagNumber.UTF8String));
            return values;
        }
        catch (AsnContentException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Minimal DER walk of RFC 6960 <c>OCSPResponse</c> → <c>BasicOCSPResponse</c> →
    /// first <c>SingleResponse.nextUpdate</c>. Signature verification of the staple is the
    /// responsibility of the full OCSP pipeline; Phase 3 gate needs only freshness.
    /// </summary>
    public static bool TryGetOcspNextUpdate(byte[] der, out DateTimeOffset nextUpdate)
    {
        nextUpdate = default;
        try
        {
            // OCSPResponse ::= SEQUENCE { responseStatus ENUMERATED, responseBytes [0] EXPLICIT ... OPTIONAL }
            var root = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
            root.ReadEnumeratedBytes(); // responseStatus
            if (!root.HasData) return false;
            var respBytesWrap = root.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
            // ResponseBytes ::= SEQUENCE { responseType OID, response OCTET STRING }
            var respBytes = respBytesWrap.ReadSequence();
            respBytes.ReadObjectIdentifier(); // id-pkix-ocsp-basic
            var basicDer = respBytes.ReadOctetString();
            // BasicOCSPResponse ::= SEQUENCE { tbsResponseData ResponseData, ... }
            var basic = new AsnReader(basicDer, AsnEncodingRules.DER).ReadSequence();
            var tbs = basic.ReadSequence(); // ResponseData
            // ResponseData ::= SEQUENCE { version [0] EXPLICIT OPTIONAL, responderID CHOICE, producedAt, responses SEQUENCE OF ... }
            var verTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            if (tbs.PeekTag().HasSameClassAndValue(verTag)) tbs.ReadSequence(verTag); // version
            var t = tbs.PeekTag(); // responderID: [1] byName / [2] byKey
            if (t.TagClass == TagClass.ContextSpecific) tbs.ReadEncodedValue();
            tbs.ReadGeneralizedTime(); // producedAt
            var responses = tbs.ReadSequence(); // SEQUENCE OF SingleResponse
            if (!responses.HasData) return false;
            var single = responses.ReadSequence();
            // SingleResponse ::= SEQUENCE { certID SEQUENCE, certStatus CHOICE, thisUpdate, nextUpdate [0] EXPLICIT OPTIONAL, ... }
            single.ReadSequence();      // certID
            single.ReadEncodedValue();  // certStatus (context-specific CHOICE)
            single.ReadGeneralizedTime(); // thisUpdate
            var nuTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            if (!single.HasData || !single.PeekTag().HasSameClassAndValue(nuTag)) return false;
            var nu = single.ReadSequence(nuTag);
            nextUpdate = nu.ReadGeneralizedTime();
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
