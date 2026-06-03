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

    /// <summary>Announce rejected: address violates the registry security profile (NPS-4 §7.2).</summary>
    public const string AnnounceProfileViolation  = "NDP-ANNOUNCE-PROFILE-VIOLATION";

    // ── NDP v0.8 error codes ──────────────────────────────────────────────────

    /// <summary>GraphFrame exceeds the 256-node / 1024-edge limit (NPS-4 §3.3).</summary>
    public const string GraphTooLarge             = "NDP-GRAPH-TOO-LARGE";

    /// <summary>GraphFrame failed structural validation (invalid NIDs, self-edge, etc.) (NPS-4 §3.3).</summary>
    public const string GraphInvalid              = "NDP-GRAPH-INVALID";

    /// <summary>Own NID detected in ndp-forwarded-by header: federation loop (NPS-4 §9).</summary>
    public const string FederationLoop            = "NDP-FEDERATION-LOOP";

    // ── NDP v0.9 error codes ──────────────────────────────────────────────────

    /// <summary>
    /// No heartbeat received within 2 × heartbeat_interval_ms; node is considered stale (NPS-4 §3.1).
    /// Registry SHOULD evict the node and notify subscribers.
    /// </summary>
    public const string AnnounceStale             = "NDP-ANNOUNCE-STALE";
}
