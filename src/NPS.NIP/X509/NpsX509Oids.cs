// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.X509;

/// <summary>
/// OID constants for NPS-RFC-0002 X.509 NID certificates. EKU OIDs and the
/// custom <c>nid-assurance-level</c> extension live under a LabAcacia IANA
/// Private Enterprise Number (PEN) arc once assigned. Until then the
/// prototype uses the documented <b>provisional</b> arc
/// <c>1.3.6.1.4.1.99999</c> reserved for testing purposes — see
/// NPS-RFC-0002 §10 OQ-2.
/// </summary>
public static class NpsX509Oids
{
    /// <summary>Provisional LabAcacia PEN arc — <b>MUST</b> be replaced once IANA assigns the real PEN.</summary>
    public const string LabAcaciaPenArc = "1.3.6.1.4.1.99999";

    /// <summary>EKU sub-arc (<c>{PEN}.1.x</c>): NPS Extended Key Usages.</summary>
    public const string EkuArc = LabAcaciaPenArc + ".1";

    /// <summary>EKU OID — cert subject is an NPS Agent (NPS-RFC-0002 §4.1).</summary>
    public const string EkuAgentIdentity        = EkuArc + ".1";

    /// <summary>EKU OID — cert subject is an NPS Node (NPS-RFC-0002 §4.1).</summary>
    public const string EkuNodeIdentity         = EkuArc + ".2";

    /// <summary>EKU OID — CA may sign <c>agent-identity</c> certs (NPS-RFC-0002 §4.1).</summary>
    public const string EkuCaIntermediateAgent  = EkuArc + ".3";

    /// <summary>Custom extension sub-arc (<c>{PEN}.2.x</c>).</summary>
    public const string ExtensionArc = LabAcaciaPenArc + ".2";

    /// <summary>
    /// Custom non-critical extension OID — encodes
    /// <see cref="AssuranceLevel"/> as an ASN.1 ENUMERATED value
    /// (0=anonymous, 1=attested, 2=verified). Non-critical so v0.1 verifiers
    /// ignore it; will flip critical when NPS-RFC-0003 enforcement lands
    /// (NPS-RFC-0002 §4.1).
    /// </summary>
    public const string NidAssuranceLevel        = ExtensionArc + ".1";

    /// <summary>RFC 8410 — Ed25519 in X.509 (algorithm identifier OID).</summary>
    public const string Ed25519                  = "1.3.101.112";
}
