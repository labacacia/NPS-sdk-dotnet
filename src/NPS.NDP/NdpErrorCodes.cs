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

    /// <summary>Resolved entry is stale and MUST NOT be served.</summary>
    public const string ResolveStale              = "NDP-RESOLVE-STALE";

    /// <summary>AnnounceFrame Ed25519 signature failed verification.</summary>
    public const string AnnounceSignatureInvalid  = "NDP-ANNOUNCE-SIGNATURE-INVALID";

    /// <summary>AnnounceFrame NID does not match the public key in the signature context.</summary>
    public const string AnnounceNidMismatch       = "NDP-ANNOUNCE-NID-MISMATCH";

    /// <summary>AnnounceFrame declares the removed legacy role <c>gateway</c>.</summary>
    public const string AnnounceRoleRemoved       = "NDP-ANNOUNCE-ROLE-REMOVED";

    /// <summary>AnnounceFrame declares an unrecognized node role.</summary>
    public const string AnnounceRoleUnknown       = "NDP-ANNOUNCE-ROLE-UNKNOWN";

    /// <summary>AnnounceFrame conflicts with an existing entry for the same <c>(nid, graph_seq)</c>.</summary>
    public const string AnnounceConflict          = "NDP-ANNOUNCE-CONFLICT";

    /// <summary>AnnounceFrame violates a registry security profile constraint.</summary>
    public const string AnnounceProfileViolation  = "NDP-ANNOUNCE-PROFILE-VIOLATION";

    /// <summary>Announce heartbeat has expired.</summary>
    public const string AnnounceStale             = "NDP-ANNOUNCE-STALE";

    /// <summary>AnnounceFrame graph_seq rolled back or repeated.</summary>
    public const string GraphSeqRollback          = "NDP-GRAPH-SEQ-ROLLBACK";

    /// <summary>GraphFrame sequence number gap detected; re-sync required.</summary>
    public const string GraphSeqGap               = "NDP-GRAPH-SEQ-GAP";

    /// <summary>GraphFrame structure is invalid.</summary>
    public const string GraphInvalid              = "NDP-GRAPH-INVALID";

    /// <summary>GraphFrame exceeds the node or edge count limits.</summary>
    public const string GraphTooLarge             = "NDP-GRAPH-TOO-LARGE";

    /// <summary>Federation forwarding detected a loop.</summary>
    public const string FederationLoop            = "NDP-FEDERATION-LOOP";

    /// <summary>AnnounceFrame issuer is not allowed by the active registry profile.</summary>
    public const string IssuerNotAllowed          = "NDP-ISSUER-NOT-ALLOWED";

    /// <summary>Active registry profile requires CA-attested NID ownership.</summary>
    public const string CaAttestRequired          = "NDP-CA-ATTEST-REQUIRED";

    /// <summary>NDP Registry is temporarily unavailable.</summary>
    public const string RegistryUnavailable       = "NDP-REGISTRY-UNAVAILABLE";
}
