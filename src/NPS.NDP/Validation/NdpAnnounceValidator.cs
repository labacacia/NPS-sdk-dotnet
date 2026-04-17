// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NDP.Frames;
using NPS.NIP.Crypto;

namespace NPS.NDP.Validation;

/// <summary>
/// Result of an <see cref="NdpAnnounceValidator"/> check.
/// </summary>
public sealed record NdpAnnounceResult
{
    /// <summary>True when the announce frame passed all validation checks.</summary>
    public bool IsValid { get; init; }

    /// <summary>NDP error code on failure (see <see cref="NdpErrorCodes"/>).</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable failure reason.</summary>
    public string? Message { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static NdpAnnounceResult Ok() => new() { IsValid = true };

    /// <summary>Creates a failed result.</summary>
    public static NdpAnnounceResult Fail(string errorCode, string message) =>
        new() { IsValid = false, ErrorCode = errorCode, Message = message };
}

/// <summary>
/// Validates an <see cref="AnnounceFrame"/> per NPS-4 §7.1.
///
/// <para>Two checks are performed:</para>
/// <list type="number">
///   <item>The frame's <c>signature</c> field verifies against the public key derived
///         from the announcing NID (the key must be registered in <see cref="KnownPublicKeys"/>).</item>
///   <item>The NID in the frame matches the NID whose public key was used to verify the signature.</item>
/// </list>
///
/// <para>To validate an announce, the receiver must already know the announcer's public key
/// (typically obtained by previously verifying their <see cref="NPS.NIP.Frames.IdentFrame"/>
/// via <see cref="NPS.NIP.Verification.NipIdentVerifier"/>).
/// Call <see cref="RegisterPublicKey"/> to supply known keys before calling <see cref="Validate"/>.</para>
/// </summary>
public sealed class NdpAnnounceValidator
{
    private readonly Dictionary<string, string> _knownPublicKeys = new();

    /// <summary>
    /// Registers a known public key for a NID.
    /// <paramref name="encodedPubKey"/> must be in <c>ed25519:{base64url}</c> format
    /// (as returned by <see cref="NipSigner.EncodePublicKey"/>).
    /// </summary>
    public void RegisterPublicKey(string nid, string encodedPubKey) =>
        _knownPublicKeys[nid] = encodedPubKey;

    /// <summary>
    /// Removes a previously registered public key (e.g. after revocation).
    /// </summary>
    public void RemovePublicKey(string nid) =>
        _knownPublicKeys.Remove(nid);

    /// <summary>
    /// Read-only view of the currently registered public keys (NID → encoded key).
    /// </summary>
    public IReadOnlyDictionary<string, string> KnownPublicKeys => _knownPublicKeys;

    /// <summary>
    /// Validates the signature and NID consistency of an <see cref="AnnounceFrame"/>.
    /// Returns <see cref="NdpAnnounceResult.Ok()"/> when both checks pass.
    /// </summary>
    public NdpAnnounceResult Validate(AnnounceFrame frame)
    {
        if (!_knownPublicKeys.TryGetValue(frame.Nid, out var encodedPubKey))
        {
            return NdpAnnounceResult.Fail(
                NdpErrorCodes.AnnounceNidMismatch,
                $"No registered public key for NID '{frame.Nid}'. " +
                "Provide the announcer's IdentFrame public key via RegisterPublicKey() first.");
        }

        var pubKey = NipSigner.DecodePublicKey(encodedPubKey);
        if (pubKey is null)
        {
            return NdpAnnounceResult.Fail(
                NdpErrorCodes.AnnounceSignatureInvalid,
                $"Failed to decode public key for NID '{frame.Nid}'.");
        }

        if (!NipSigner.Verify(pubKey, frame, frame.Signature))
        {
            return NdpAnnounceResult.Fail(
                NdpErrorCodes.AnnounceSignatureInvalid,
                $"AnnounceFrame signature verification failed for NID '{frame.Nid}'.");
        }

        return NdpAnnounceResult.Ok();
    }
}
