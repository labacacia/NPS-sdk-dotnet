// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NSec.Cryptography;

namespace NPS.NIP.X509;

/// <summary>
/// Builds X.509 leaf and intermediate certificates per NPS-RFC-0002 §4.1.
/// Subject CN and SAN URI carry the NID; EKU is marked critical and contains
/// exactly one of <see cref="NpsX509Oids.EkuAgentIdentity"/> /
/// <see cref="NpsX509Oids.EkuNodeIdentity"/> /
/// <see cref="NpsX509Oids.EkuCaIntermediateAgent"/>; the
/// <see cref="NpsX509Oids.NidAssuranceLevel"/> custom non-critical extension
/// encodes the assurance level as ASN.1 ENUMERATED.
/// </summary>
public static class NipX509Builder
{
    /// <summary>The role this leaf cert attests to. Maps 1:1 to an EKU OID.</summary>
    public enum LeafRole
    {
        Agent,
        Node,
    }

    /// <summary>
    /// Issues a leaf X.509 cert binding <paramref name="subjectNid"/> to
    /// <paramref name="subjectPubKeyRaw"/>, signed by
    /// <paramref name="caPrivateKey"/> on behalf of <paramref name="issuerNid"/>.
    /// </summary>
    public static X509Certificate2 IssueLeaf(
        string         subjectNid,
        byte[]         subjectPubKeyRaw,
        Key            caPrivateKey,
        string         issuerNid,
        LeafRole       role,
        AssuranceLevel assuranceLevel,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        byte[]         serialNumber)
    {
        if (subjectPubKeyRaw is null || subjectPubKeyRaw.Length != 32)
            throw new ArgumentException("Ed25519 subject pubkey must be 32 bytes.", nameof(subjectPubKeyRaw));

        var subjectName    = new X500DistinguishedName($"CN={EscapeDn(subjectNid)}");
        var subjectPubKey  = Ed25519PublicKey.FromRaw(subjectPubKeyRaw);
        var req            = new CertificateRequest(subjectName, subjectPubKey, HashAlgorithmName.SHA256);

        // BasicConstraints — leaf cert: cA = false.
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0,
            critical: true));

        // KeyUsage — digitalSignature only (Ed25519 signing).
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, critical: true));

        // ExtendedKeyUsage — RFC-0002 §4.1 mandates critical.
        var ekuOid = role switch
        {
            LeafRole.Agent => NpsX509Oids.EkuAgentIdentity,
            LeafRole.Node  => NpsX509Oids.EkuNodeIdentity,
            _              => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(ekuOid) }, critical: true));

        // Subject Alternative Name — URI = NID (RFC 5280 §4.2.1.6 compliance).
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddUri(new Uri(subjectNid, UriKind.Absolute));
        req.CertificateExtensions.Add(sanBuilder.Build(critical: false));

        // Custom extension — id-nid-assurance-level (NPS-RFC-0002 §4.1; non-critical for v0.1).
        req.CertificateExtensions.Add(BuildAssuranceLevelExtension(assuranceLevel));

        var generator   = new Ed25519X509SignatureGenerator(caPrivateKey);
        var issuerName  = new X500DistinguishedName($"CN={EscapeDn(issuerNid)}");
        return req.Create(issuerName, generator, notBefore, notAfter, serialNumber);
    }

    /// <summary>
    /// Issues a self-signed CA root certificate. Used by the prototype to
    /// stand up a single-level test PKI; production deployments would have
    /// the root signed offline and ship as a trust bundle.
    /// </summary>
    public static X509Certificate2 IssueRoot(
        string         caNid,
        Key            caPrivateKey,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        byte[]         serialNumber)
    {
        var caPubRaw      = caPrivateKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var subjectName   = new X500DistinguishedName($"CN={EscapeDn(caNid)}");
        var subjectPubKey = Ed25519PublicKey.FromRaw(caPubRaw);
        var req           = new CertificateRequest(subjectName, subjectPubKey, HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 1,
            critical: true));

        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            critical: true));

        // Per RFC-0002 §4.1, a CA that may sign agent-identity certs declares
        // EKU `ca-intermediate-agent`. Critical so a generic TLS chain
        // verifier won't mistake it for a TLS root.
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(NpsX509Oids.EkuCaIntermediateAgent) }, critical: true));

        var generator = new Ed25519X509SignatureGenerator(caPrivateKey);
        return req.Create(subjectName, generator, notBefore, notAfter, serialNumber);
    }

    private static X509Extension BuildAssuranceLevelExtension(AssuranceLevel level)
    {
        // ASN.1: ENUMERATED { anonymous(0), attested(1), verified(2) }
        var w = new AsnWriter(AsnEncodingRules.DER);
        w.WriteEnumeratedValue(MapToAsn(level));
        return new X509Extension(
            new Oid(NpsX509Oids.NidAssuranceLevel),
            w.Encode(),
            critical: false);
    }

    private static AssuranceLevelAsn MapToAsn(AssuranceLevel level) => level switch
    {
        AssuranceLevel.Anonymous => AssuranceLevelAsn.Anonymous,
        AssuranceLevel.Attested  => AssuranceLevelAsn.Attested,
        AssuranceLevel.Verified  => AssuranceLevelAsn.Verified,
        _                         => AssuranceLevelAsn.Anonymous,
    };

    /// <summary>ASN.1 ENUMERATED encoding for the <c>nid-assurance-level</c> extension.</summary>
    private enum AssuranceLevelAsn
    {
        Anonymous = 0,
        Attested  = 1,
        Verified  = 2,
    }

    /// <summary>
    /// Escapes characters in the NID that have special meaning inside an
    /// X.500 DN string (RFC 4514). NIDs use <c>:</c> heavily so we must
    /// escape it; <c>=</c>, <c>,</c>, <c>+</c> etc. are uncommon but
    /// handled defensively.
    /// </summary>
    private static string EscapeDn(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
