// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.Http;

/// <summary>
/// Standard HTTP header name constants for the NWP overlay (NPS-2 §8).
/// </summary>
public static class NwpHttpHeaders
{
    // ── Request headers ──────────────────────────────────────────────────────

    /// <summary>
    /// Agent NID in <c>urn:nps:agent:{ca-host}:{id}</c> format.
    /// Required when <c>auth.required == true</c> in the node's NWM.
    /// </summary>
    public const string Agent    = "X-NWP-Agent";

    /// <summary>
    /// Token budget upper limit in NPT (uint32). Node SHOULD trim the response
    /// to stay within budget, or return <c>NWP-BUDGET-EXCEEDED</c> if impossible.
    /// </summary>
    public const string Budget   = "X-NWP-Budget";

    /// <summary>
    /// Node graph traversal depth (uint, default 1, max 5).
    /// </summary>
    public const string Depth    = "X-NWP-Depth";

    /// <summary>
    /// Requested payload encoding tier: <c>"json"</c> or <c>"msgpack"</c>.
    /// Defaults to <c>"msgpack"</c> when absent.
    /// </summary>
    public const string Encoding = "X-NWP-Encoding";

    /// <summary>
    /// Agent's tokenizer identifier (e.g. <c>"cl100k_base"</c>, <c>"claude"</c>).
    /// Used for NPT → native token conversion (NPS-2 §8).
    /// </summary>
    public const string Tokenizer = "X-NWP-Tokenizer";

    // ── Response headers ─────────────────────────────────────────────────────

    /// <summary>The <c>anchor_id</c> of the schema used in the response payload.</summary>
    public const string Schema   = "X-NWP-Schema";

    /// <summary>Actual NPT consumption for the response payload.</summary>
    public const string Tokens   = "X-NWP-Tokens";

    /// <summary>Native token consumption (when the Agent's tokenizer is known).</summary>
    public const string TokensNative = "X-NWP-Tokens-Native";

    /// <summary>Tokenizer identifier actually used for token calculation.</summary>
    public const string TokenizerUsed = "X-NWP-Tokenizer-Used";

    /// <summary><c>"true"</c> when the response was served from the node's server-side cache.</summary>
    public const string Cached   = "X-NWP-Cached";

    /// <summary>Node type of the responding server: <c>"memory"</c>, <c>"action"</c>, or <c>"complex"</c>.</summary>
    public const string NodeType = "X-NWP-Node-Type";

    // ── MIME types ───────────────────────────────────────────────────────────

    /// <summary>MIME type for NWP request frames (<c>Content-Type</c> on requests).</summary>
    public const string MimeFrame    = "application/nwp-frame";

    /// <summary>MIME type for NWP capsule responses (<c>Content-Type</c> on responses).</summary>
    public const string MimeCapsule  = "application/nwp-capsule";

    /// <summary>MIME type for Neural Web Manifest responses.</summary>
    public const string MimeManifest = "application/nwp-manifest+json";
}

/// <summary>
/// NWP protocol error codes (NPS-2 §11).
/// </summary>
public static class NwpErrorCodes
{
    // Auth
    public const string AuthNidScopeViolation    = "NWP-AUTH-NID-SCOPE-VIOLATION";
    public const string AuthNidExpired           = "NWP-AUTH-NID-EXPIRED";
    public const string AuthNidRevoked           = "NWP-AUTH-NID-REVOKED";
    public const string AuthNidUntrustedIssuer   = "NWP-AUTH-NID-UNTRUSTED-ISSUER";
    public const string AuthNidCapabilityMissing = "NWP-AUTH-NID-CAPABILITY-MISSING";

    /// <summary>
    /// Agent's <c>assurance_level</c> is below the node's
    /// <c>min_assurance_level</c> (NWM §4.1) or per-action override
    /// (§4.6). Response SHOULD include a <c>hint</c> pointing to a CA
    /// enrolment URL. NPS-RFC-0003. → NPS-AUTH-FORBIDDEN.
    /// </summary>
    public const string AuthAssuranceTooLow     = "NWP-AUTH-ASSURANCE-TOO-LOW";

    /// <summary>
    /// Receiving Node's <c>reputation_policy</c> matched a
    /// <c>reject_on</c> rule against the requesting <c>subject_nid</c>.
    /// Reserved at NWP v0.7 (Phase 1 of NPS-RFC-0004); the policy
    /// field shape that produces this error lands at NWP v0.8 (Phase
    /// 2). → NPS-AUTH-FORBIDDEN.
    /// </summary>
    public const string AuthReputationBlocked   = "NWP-AUTH-REPUTATION-BLOCKED";

    // Query
    public const string QueryFilterInvalid       = "NWP-QUERY-FILTER-INVALID";
    public const string QueryFieldUnknown        = "NWP-QUERY-FIELD-UNKNOWN";
    public const string QueryCursorInvalid       = "NWP-QUERY-CURSOR-INVALID";

    // Action
    public const string ActionNotFound              = "NWP-ACTION-NOT-FOUND";
    public const string ActionParamsInvalid          = "NWP-ACTION-PARAMS-INVALID";
    public const string ActionIdempotencyConflict    = "NWP-ACTION-IDEMPOTENCY-CONFLICT";
    public const string TaskNotFound                 = "NWP-TASK-NOT-FOUND";
    public const string TaskAlreadyCancelled         = "NWP-TASK-ALREADY-CANCELLED";

    // Capacity / graph
    public const string BudgetExceeded           = "NWP-BUDGET-EXCEEDED";
    public const string DepthExceeded            = "NWP-DEPTH-EXCEEDED";
    public const string GraphCycle               = "NWP-GRAPH-CYCLE";
    public const string NodeUnavailable          = "NWP-NODE-UNAVAILABLE";

    // Manifest
    public const string ManifestVersionUnsupported = "NWP-MANIFEST-VERSION-UNSUPPORTED";
}
