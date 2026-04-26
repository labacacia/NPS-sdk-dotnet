// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP.Crypto;
using NSec.Cryptography;

namespace NPS.NIP.Reputation;

/// <summary>
/// Sign / verify helpers for <see cref="ReputationLogEntry"/>. Wraps
/// the existing <see cref="NipSigner"/> Ed25519 + JCS-style canonical
/// JSON pipeline (NPS-3 §5.1.2 / NPS-RFC-0004 §4.1) — the <c>signature</c>
/// field is excluded from the signed canonical form by the underlying
/// signer, so callers may set it to any placeholder before signing.
/// <para>
/// Phase 1 ships the issuer-side signature only (the first half of the
/// dual-signature model). The log-operator-side ordering signature is
/// applied by the log operator at append time and lives in the
/// operator's own surface (Phase 2).
/// </para>
/// </summary>
public static class ReputationLogEntrySigner
{
    /// <summary>
    /// Produces an Ed25519 signature over the canonical form of
    /// <paramref name="entry"/> with the <c>signature</c> field
    /// excluded, returning a new entry with the signature populated.
    /// </summary>
    /// <param name="issuerKey">
    /// Private key of <see cref="ReputationLogEntry.IssuerNid"/>.
    /// </param>
    /// <param name="entry">
    /// Entry to sign. The current value of
    /// <see cref="ReputationLogEntry.Signature"/> is ignored — set it
    /// to any non-null placeholder (e.g. <c>""</c>) before calling.
    /// </param>
    /// <returns>The same entry with <c>Signature</c> populated.</returns>
    public static ReputationLogEntry Sign(Key issuerKey, ReputationLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(issuerKey);
        ArgumentNullException.ThrowIfNull(entry);

        var signature = NipSigner.Sign(issuerKey, entry);
        return entry with { Signature = signature };
    }

    /// <summary>
    /// Verifies the issuer-side signature on <paramref name="entry"/>
    /// against <paramref name="issuerPubKey"/>. Returns <c>true</c>
    /// iff the signature is well-formed and valid for the canonical
    /// form of the entry minus its <c>signature</c> field.
    /// </summary>
    public static bool Verify(PublicKey issuerPubKey, ReputationLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(issuerPubKey);
        ArgumentNullException.ThrowIfNull(entry);
        return NipSigner.Verify(issuerPubKey, entry, entry.Signature);
    }
}
