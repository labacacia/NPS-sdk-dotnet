// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NDP;

/// <summary>NDP error codes (NPS-4 §6).</summary>
public static class NdpErrorCodes
{
    /// <summary>nwp:// address could not be resolved (no matching registry entry).</summary>
    public const string ResolveNotFound           = "NDP-RESOLVE-NOT-FOUND";

    /// <summary>Resolution result is ambiguous (multiple conflicting registrations).</summary>
    public const string ResolveAmbiguous          = "NDP-RESOLVE-AMBIGUOUS";

    /// <summary>Resolve request timed out.</summary>
    public const string ResolveTimeout            = "NDP-RESOLVE-TIMEOUT";

    /// <summary>AnnounceFrame Ed25519 signature failed verification.</summary>
    public const string AnnounceSignatureInvalid  = "NDP-ANNOUNCE-SIGNATURE-INVALID";

    /// <summary>AnnounceFrame NID does not match the public key in the signature context.</summary>
    public const string AnnounceNidMismatch       = "NDP-ANNOUNCE-NID-MISMATCH";

    /// <summary>GraphFrame sequence number gap detected; re-sync required.</summary>
    public const string GraphSeqGap               = "NDP-GRAPH-SEQ-GAP";

    /// <summary>NDP Registry is temporarily unavailable.</summary>
    public const string RegistryUnavailable       = "NDP-REGISTRY-UNAVAILABLE";
}
