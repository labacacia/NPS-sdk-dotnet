// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Core;

/// <summary>
/// NPS native status code constants (see status-codes.md).
/// These are transport-level status codes independent of HTTP.
/// Protocol-specific error codes (NCP-*, NWP-*, etc.) map to these.
/// </summary>
public static class NpsStatusCodes
{
    // ── Success ──────────────────────────────────────────────────────────────
    public const string Ok          = "NPS-OK";
    public const string OkAccepted  = "NPS-OK-ACCEPTED";
    public const string OkNoContent = "NPS-OK-NO-CONTENT";

    // ── Client errors ────────────────────────────────────────────────────────
    public const string ClientBadFrame      = "NPS-CLIENT-BAD-FRAME";
    public const string ClientBadParam      = "NPS-CLIENT-BAD-PARAM";
    public const string ClientNotFound      = "NPS-CLIENT-NOT-FOUND";
    public const string ClientConflict      = "NPS-CLIENT-CONFLICT";
    public const string ClientGone          = "NPS-CLIENT-GONE";
    public const string ClientUnprocessable = "NPS-CLIENT-UNPROCESSABLE";

    // ── Auth ─────────────────────────────────────────────────────────────────
    public const string AuthUnauthenticated = "NPS-AUTH-UNAUTHENTICATED";
    public const string AuthForbidden       = "NPS-AUTH-FORBIDDEN";

    // ── Limits ───────────────────────────────────────────────────────────────
    public const string LimitRate    = "NPS-LIMIT-RATE";
    public const string LimitBudget  = "NPS-LIMIT-BUDGET";
    public const string LimitPayload = "NPS-LIMIT-PAYLOAD";

    // ── Server errors ────────────────────────────────────────────────────────
    public const string ServerInternal            = "NPS-SERVER-INTERNAL";
    public const string ServerUnavailable         = "NPS-SERVER-UNAVAILABLE";
    public const string ServerTimeout             = "NPS-SERVER-TIMEOUT";
    public const string ServerEncodingUnsupported = "NPS-SERVER-ENCODING-UNSUPPORTED";

    /// <summary>
    /// A required downstream service that this node depends on
    /// (reputation log operator, external auth service, etc.) is
    /// unreachable. Distinct from <see cref="ServerUnavailable"/> so
    /// that clients can retry against an alternate downstream when
    /// policy permits. (NPS-RFC-0004)
    /// </summary>
    public const string DownstreamUnavailable     = "NPS-DOWNSTREAM-UNAVAILABLE";

    // ── Stream errors ────────────────────────────────────────────────────────
    public const string StreamSeqGap   = "NPS-STREAM-SEQ-GAP";
    public const string StreamNotFound = "NPS-STREAM-NOT-FOUND";
    public const string StreamLimit    = "NPS-STREAM-LIMIT";

    // ── Protocol errors ──────────────────────────────────────────────────────
    /// <summary>Client min_version is incompatible with the Server's supported NPS version.</summary>
    public const string ProtoVersionIncompatible = "NPS-PROTO-VERSION-INCOMPATIBLE";

    /// <summary>
    /// Native-mode connection opened with bytes other than the constant
    /// preamble <c>b"NPS/1.0\n"</c>. Server closes silently within 500 ms;
    /// no ErrorFrame is emitted on the wire (NPS-RFC-0001 / NPS-1 NCP §2.6.1).
    /// This constant exists for SDK-internal telemetry classification.
    /// </summary>
    public const string ProtoPreambleInvalid = "NPS-PROTO-PREAMBLE-INVALID";
}
