// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using NPS.NIP.Ca;
using NSec.Cryptography;

namespace NPS.NIP.X509;

/// <summary>
/// Validates an X.509 cert chain produced by <see cref="NipX509Builder"/>
/// against the NPS-RFC-0002 §4.1 constraints: critical NPS EKU, SAN URI
/// matches the asserted NID, leaf signature chains up to a trusted root,
/// and the optional <c>nid-assurance-level</c> extension matches the
/// caller-asserted assurance level when both are present.
/// </summary>
public static class NipX509Verifier
{
    /// <summary>
    /// Validate a base64url DER-encoded chain. Element 0 is the leaf;
    /// subsequent elements are intermediates up to (but not including)
    /// a trusted root. The trusted root is supplied via
    /// <paramref name="trustedRootCerts"/>.
    /// </summary>
    public static NipX509VerifyResult Verify(
        IReadOnlyList<string>            certChainBase64UrlDer,
        string                            assertedNid,
        AssuranceLevel?                   assertedAssuranceLevel,
        IReadOnlyList<X509Certificate2>   trustedRootCerts)
    {
        if (certChainBase64UrlDer is null || certChainBase64UrlDer.Count == 0)
            return NipX509VerifyResult.Fail(
                NipErrorCodes.CertFormatInvalid,
                "cert_chain is empty.");

        var chain = new List<X509Certificate2>(certChainBase64UrlDer.Count);
        foreach (var b64u in certChainBase64UrlDer)
        {
            try
            {
                var der = Base64Url(b64u);
                chain.Add(X509CertificateLoader.LoadCertificate(der));
            }
            catch (Exception ex)
            {
                return NipX509VerifyResult.Fail(
                    NipErrorCodes.CertFormatInvalid,
                    $"cert_chain element failed DER decode: {ex.Message}");
            }
        }

        var leaf = chain[0];

        // 1. EKU — leaf MUST have one of agent-identity / node-identity, marked critical.
        var ekuCheck = CheckLeafEku(leaf);
        if (ekuCheck is not null) return ekuCheck;

        // 2. SAN URI MUST match the asserted NID; subject CN SHOULD match (defence in depth).
        var sanCheck = CheckSubjectNid(leaf, assertedNid);
        if (sanCheck is not null) return sanCheck;

        // 3. Optional assurance-level extension — when present, MUST equal asserted.
        var alCheck = CheckAssuranceLevel(leaf, assertedAssuranceLevel);
        if (alCheck is not null) return alCheck;

        // 4. Chain validity — leaf signed by an intermediate (or root), up to a trusted root.
        var chainCheck = CheckChainSignature(chain, trustedRootCerts);
        if (chainCheck is not null) return chainCheck;

        return NipX509VerifyResult.Ok(leaf);
    }

    // ── EKU ─────────────────────────────────────────────────────────────────

    private static NipX509VerifyResult? CheckLeafEku(X509Certificate2 leaf)
    {
        var eku = leaf.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .FirstOrDefault();
        if (eku is null)
        {
            return NipX509VerifyResult.Fail(
                NipErrorCodes.CertEkuMissing,
                "leaf certificate is missing the ExtendedKeyUsage extension.");
        }
        if (!eku.Critical)
        {
            // Per RFC-0002 §4.1, NPS EKU MUST be critical.
            return NipX509VerifyResult.Fail(
                NipErrorCodes.CertEkuMissing,
                "leaf certificate ExtendedKeyUsage is not marked critical.");
        }

        var hasAgent = eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()
            .Any(o => o.Value == NpsX509Oids.EkuAgentIdentity || o.Value == NpsX509Oids.EkuNodeIdentity);
        if (!hasAgent)
        {
            return NipX509VerifyResult.Fail(
                NipErrorCodes.CertEkuMissing,
                "leaf certificate EKU does not include agent-identity or node-identity.");
        }
        return null;
    }

    // ── Subject / SAN match ─────────────────────────────────────────────────

    private static NipX509VerifyResult? CheckSubjectNid(X509Certificate2 leaf, string assertedNid)
    {
        // Subject CN match.
        var cn = ExtractCn(leaf.SubjectName);
        if (cn is null || !cn.Equals(assertedNid, StringComparison.Ordinal))
        {
            return NipX509VerifyResult.Fail(
                NipErrorCodes.CertSubjectNidMismatch,
                $"leaf certificate subject CN '{cn}' does not match asserted NID '{assertedNid}'.");
        }

        // SAN URI match.
        var sanExt = leaf.Extensions
            .FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        if (sanExt is null)
        {
            return NipX509VerifyResult.Fail(
                NipErrorCodes.CertSubjectNidMismatch,
                "leaf certificate is missing the Subject Alternative Name extension.");
        }
        var sanUris = ExtractSanUris(sanExt.RawData);
        if (!sanUris.Contains(assertedNid))
        {
            return NipX509VerifyResult.Fail(
                NipErrorCodes.CertSubjectNidMismatch,
                $"leaf SAN URIs [{string.Join(",", sanUris)}] do not contain '{assertedNid}'.");
        }
        return null;
    }

    private static string? ExtractCn(X500DistinguishedName dn)
    {
        // X500DistinguishedName's GetSinglyEncodedRdns isn't trivially accessible;
        // fall back to the formatted string — the prototype always uses CN=<NID>.
        var formatted = dn.Format(false);
        const string prefix = "CN=";
        var idx = formatted.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = formatted[(idx + prefix.Length)..];
        // Strip trailing ", " separators if any; our DNs are CN-only so usually none.
        var commaIdx = rest.IndexOf(", ", StringComparison.Ordinal);
        return commaIdx >= 0 ? rest[..commaIdx] : rest;
    }

    private static List<string> ExtractSanUris(byte[] sanDer)
    {
        var uris = new List<string>();
        try
        {
            var reader = new AsnReader(sanDer, AsnEncodingRules.DER);
            var seq = reader.ReadSequence();
            while (seq.HasData)
            {
                // GeneralName CHOICE — URI is [6] IA5String.
                var tag = seq.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6)
                {
                    uris.Add(seq.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                }
                else
                {
                    seq.ReadEncodedValue();   // skip non-URI GeneralNames
                }
            }
        }
        catch
        {
            // Caller treats empty-list as "no match"; we only get here if the
            // SAN itself is malformed, which is also a mismatch.
        }
        return uris;
    }

    // ── Assurance level ─────────────────────────────────────────────────────

    private static NipX509VerifyResult? CheckAssuranceLevel(
        X509Certificate2 leaf, AssuranceLevel? asserted)
    {
        if (asserted is null) return null;   // nothing to enforce

        var ext = leaf.Extensions
            .FirstOrDefault(e => e.Oid?.Value == NpsX509Oids.NidAssuranceLevel);
        if (ext is null) return null;        // optional: no extension means no contradiction

        // Require canonical 3-byte DER ENUMERATED (0A 01 XX) to match TS/Go/Rust strictness.
        if (ext.RawData.Length != 3 || ext.RawData[0] != 0x0A || ext.RawData[1] != 0x01)
            return NipX509VerifyResult.Fail(
                NipErrorCodes.CertFormatInvalid,
                "nid-assurance-level extension is not in canonical DER ENUMERATED form (expected 0A 01 XX).");

        try
        {
            var reader = new AsnReader(ext.RawData, AsnEncodingRules.DER);
            var asnLevel = (int)reader.ReadEnumeratedValue<AssuranceLevelAsn>();
            var inferred = asnLevel switch
            {
                0 => AssuranceLevel.Anonymous,
                1 => AssuranceLevel.Attested,
                2 => AssuranceLevel.Verified,
                _ => (AssuranceLevel?)null,
            };
            if (inferred is null)
            {
                return NipX509VerifyResult.Fail(
                    NipErrorCodes.AssuranceUnknown,
                    $"id-nid-assurance-level extension carried unknown value {asnLevel}.");
            }
            if (inferred != asserted)
            {
                return NipX509VerifyResult.Fail(
                    NipErrorCodes.AssuranceMismatch,
                    $"asserted assurance level {asserted} disagrees with X.509 extension {inferred}.");
            }
        }
        catch
        {
            return NipX509VerifyResult.Fail(
                NipErrorCodes.CertFormatInvalid,
                "id-nid-assurance-level extension is malformed.");
        }
        return null;
    }

    private enum AssuranceLevelAsn { Anonymous = 0, Attested = 1, Verified = 2 }

    // ── Chain signature ─────────────────────────────────────────────────────

    private static NipX509VerifyResult? CheckChainSignature(
        IReadOnlyList<X509Certificate2> chain,
        IReadOnlyList<X509Certificate2> trustedRoots)
    {
        // Walk the chain leaf → intermediates → root. Each step verifies the
        // child's TBS bytes against the parent's Ed25519 public key.
        for (int i = 0; i < chain.Count; i++)
        {
            var child  = chain[i];
            var parent = i + 1 < chain.Count ? chain[i + 1] : FindIssuer(child, trustedRoots);
            if (parent is null)
            {
                return NipX509VerifyResult.Fail(
                    NipErrorCodes.CertFormatInvalid,
                    $"no trusted root found for issuer '{child.IssuerName.Name}'.");
            }
            if (!VerifyEd25519CertSignature(child, parent))
            {
                return NipX509VerifyResult.Fail(
                    NipErrorCodes.CertFormatInvalid,
                    $"signature on certificate '{child.SubjectName.Name}' did not verify under issuer '{parent.SubjectName.Name}'.");
            }
        }
        return null;
    }

    private static X509Certificate2? FindIssuer(X509Certificate2 child, IReadOnlyList<X509Certificate2> trusted)
    {
        foreach (var root in trusted)
        {
            // Subject DN match is sufficient for the prototype; production
            // would also verify Authority Key Identifier when present.
            if (string.Equals(child.IssuerName.Name, root.SubjectName.Name, StringComparison.Ordinal))
                return root;
        }
        return null;
    }

    private static bool VerifyEd25519CertSignature(X509Certificate2 child, X509Certificate2 parent)
    {
        try
        {
            var (tbsBytes, sigBytes) = ExtractTbsAndSignature(child.RawData);

            var parentRawPub = Ed25519PublicKey.ExtractRaw(parent.PublicKey);
            if (parentRawPub is null) return false;

            var parentPub = NSec.Cryptography.PublicKey.Import(
                SignatureAlgorithm.Ed25519, parentRawPub, KeyBlobFormat.RawPublicKey);

            return SignatureAlgorithm.Ed25519.Verify(parentPub, tbsBytes, sigBytes);
        }
        catch { return false; }
    }

    /// <summary>
    /// Extracts the TBSCertificate bytes (signed payload) and the BIT STRING
    /// signature from a DER-encoded <c>Certificate</c> SEQUENCE per RFC 5280.
    /// </summary>
    private static (byte[] tbs, byte[] signature) ExtractTbsAndSignature(byte[] certDer)
    {
        var reader   = new AsnReader(certDer, AsnEncodingRules.DER);
        var certSeq  = reader.ReadSequence();

        // TBSCertificate is the first inner SEQUENCE — read its raw encoded form.
        var tbsBytes = certSeq.ReadEncodedValue().ToArray();

        certSeq.ReadSequence();   // skip signatureAlgorithm

        var sig = certSeq.ReadBitString(out _);
        return (tbsBytes, sig);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static byte[] Base64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// Outcome of <see cref="NipX509Verifier.Verify"/>. On success, <see cref="Leaf"/>
/// is the parsed leaf certificate so callers can read additional fields
/// (NotAfter, public key, custom extensions) without re-parsing.
/// </summary>
public sealed class NipX509VerifyResult
{
    public bool                Valid     { get; private init; }
    public string?             ErrorCode { get; private init; }
    public string?             Message   { get; private init; }
    public X509Certificate2?   Leaf      { get; private init; }

    public static NipX509VerifyResult Ok(X509Certificate2 leaf) =>
        new() { Valid = true, Leaf = leaf };

    public static NipX509VerifyResult Fail(string errorCode, string message) =>
        new() { Valid = false, ErrorCode = errorCode, Message = message };
}
