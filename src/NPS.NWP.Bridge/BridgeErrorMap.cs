// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core;

namespace NPS.NWP.Bridge;

/// <summary>
/// The normative NPS ↔ foreign-protocol error mapping of NWP §16.3 (NPS-CR-0010).
///
/// <para>
/// Through alpha.15 this mapping was implemented twice per protocol — once in the
/// outbound dispatcher, once in the then-separate <c>compat/*-ingress</c> package —
/// and the two copies drifted. §16.3 makes the mapping normative and requires a
/// <b>single</b> implementation to serve both directions. This is that implementation;
/// no inbound or outbound path may hand-roll its own.
/// </para>
/// </summary>
public static class BridgeErrorMap
{
    /// <summary>
    /// NPS status → JSON-RPC 2.0 error code (MCP, A2A). NWP §16.3.
    /// </summary>
    /// <param name="npsStatus">An <see cref="NpsStatusCodes"/> value.</param>
    /// <param name="resourceRead">
    /// When <c>true</c>, the request was a <c>resources/read</c>, where an unknown target is a
    /// bad <i>argument</i> (-32602) rather than a missing <i>method</i> (-32601). §16.3 calls this
    /// distinction out explicitly: an unknown tool is a missing method to an MCP client; an
    /// unknown resource is not.
    /// </param>
    public static int ToJsonRpc(string? npsStatus, bool resourceRead = false) => npsStatus switch
    {
        NpsStatusCodes.ClientBadFrame => BridgeJsonRpcErrorCodes.InvalidRequest,

        NpsStatusCodes.ClientBadParam
            or NpsStatusCodes.ClientUnprocessable
            or NpsStatusCodes.ClientGone => BridgeJsonRpcErrorCodes.InvalidParams,

        NpsStatusCodes.ClientNotFound => resourceRead
            ? BridgeJsonRpcErrorCodes.InvalidParams
            : BridgeJsonRpcErrorCodes.MethodNotFound,

        NpsStatusCodes.ClientConflict => BridgeJsonRpcErrorCodes.Conflict,

        NpsStatusCodes.AuthUnauthenticated => BridgeJsonRpcErrorCodes.Unauthenticated,
        NpsStatusCodes.AuthForbidden => BridgeJsonRpcErrorCodes.Forbidden,

        NpsStatusCodes.LimitRate
            or NpsStatusCodes.LimitBudget
            or NpsStatusCodes.LimitPayload => BridgeJsonRpcErrorCodes.LimitExceeded,

        // "Not implemented" reads to an MCP client as a method it cannot call.
        NpsStatusCodes.ServerUnsupported => BridgeJsonRpcErrorCodes.MethodNotFound,

        _ => BridgeJsonRpcErrorCodes.InternalError,
    };

    /// <summary>
    /// NPS status → gRPC status code name. NWP §16.3.
    /// </summary>
    public static string ToGrpcStatus(string? npsStatus) => npsStatus switch
    {
        NpsStatusCodes.ClientBadFrame
            or NpsStatusCodes.ClientBadParam
            or NpsStatusCodes.ClientUnprocessable => "INVALID_ARGUMENT",

        NpsStatusCodes.ClientNotFound
            or NpsStatusCodes.ClientGone => "NOT_FOUND",

        NpsStatusCodes.ClientConflict => "ABORTED",

        NpsStatusCodes.AuthUnauthenticated => "UNAUTHENTICATED",
        NpsStatusCodes.AuthForbidden => "PERMISSION_DENIED",

        NpsStatusCodes.LimitRate
            or NpsStatusCodes.LimitBudget
            or NpsStatusCodes.LimitPayload => "RESOURCE_EXHAUSTED",

        NpsStatusCodes.ServerUnsupported => "UNIMPLEMENTED",
        NpsStatusCodes.ServerInternal => "INTERNAL",

        NpsStatusCodes.ServerUnavailable
            or NpsStatusCodes.DownstreamUnavailable => "UNAVAILABLE",

        NpsStatusCodes.ServerTimeout => "DEADLINE_EXCEEDED",

        _ => "INTERNAL",
    };

    /// <summary>
    /// HTTP status → NPS status. The inverse direction, used when translating an upstream
    /// response back into NWP frames. §16.3 requires the <b>most specific</b> NPS status where
    /// the inverse is not injective — never a blanket <see cref="NpsStatusCodes.ServerInternal"/>.
    /// </summary>
    public static string FromHttpStatus(int httpStatus) => httpStatus switch
    {
        400 => NpsStatusCodes.ClientBadParam,
        401 => NpsStatusCodes.AuthUnauthenticated,
        403 => NpsStatusCodes.AuthForbidden,
        404 => NpsStatusCodes.ClientNotFound,
        408 => NpsStatusCodes.ServerTimeout,
        409 => NpsStatusCodes.ClientConflict,
        410 => NpsStatusCodes.ClientGone,
        413 => NpsStatusCodes.LimitPayload,
        415 => NpsStatusCodes.ServerEncodingUnsupported,
        422 => NpsStatusCodes.ClientUnprocessable,
        429 => NpsStatusCodes.LimitRate,
        501 => NpsStatusCodes.ServerUnsupported,
        502 or 504 => NpsStatusCodes.DownstreamUnavailable,
        503 => NpsStatusCodes.ServerUnavailable,
        _ when httpStatus >= 500 => NpsStatusCodes.ServerInternal,
        _ when httpStatus >= 400 => NpsStatusCodes.ClientBadParam,
        _ => NpsStatusCodes.Ok,
    };

    /// <summary>
    /// Reverse of <see cref="ToJsonRpc"/>: a foreign JSON-RPC error code → the most specific NPS
    /// status. Used by an <b>outbound</b> Bridge translating an upstream MCP/A2A server's error
    /// back into NWP. §16.3 requires the most specific status where the inverse is not injective —
    /// never a blanket <see cref="NpsStatusCodes.ServerInternal"/> — so the application-defined
    /// codes this implementation emits (-32001/-32003/-32004/-32005) recover their exact NPS class,
    /// and the ambiguous standard codes pick the client-facing reading an agent can act on.
    /// </summary>
    public static string FromJsonRpc(int jsonRpcCode) => jsonRpcCode switch
    {
        BridgeJsonRpcErrorCodes.ParseError => NpsStatusCodes.ClientBadFrame,
        BridgeJsonRpcErrorCodes.InvalidRequest => NpsStatusCodes.ClientBadFrame,
        BridgeJsonRpcErrorCodes.MethodNotFound => NpsStatusCodes.ClientNotFound,
        BridgeJsonRpcErrorCodes.InvalidParams => NpsStatusCodes.ClientBadParam,
        BridgeJsonRpcErrorCodes.InternalError => NpsStatusCodes.ServerInternal,
        BridgeJsonRpcErrorCodes.Unauthenticated => NpsStatusCodes.AuthUnauthenticated,
        BridgeJsonRpcErrorCodes.Forbidden => NpsStatusCodes.AuthForbidden,
        BridgeJsonRpcErrorCodes.Conflict => NpsStatusCodes.ClientConflict,
        BridgeJsonRpcErrorCodes.LimitExceeded => NpsStatusCodes.LimitRate,
        BridgeJsonRpcErrorCodes.UpstreamError => NpsStatusCodes.DownstreamUnavailable,
        _ => NpsStatusCodes.ServerInternal,
    };

    /// <summary>
    /// Reverse of <see cref="ToGrpcStatus"/>: a gRPC status code name → the most specific NPS
    /// status. Used by an <b>outbound</b> Bridge translating an upstream gRPC service's status
    /// back into NWP (§16.3). Accepts the canonical <c>UPPER_SNAKE</c> spelling.
    /// </summary>
    public static string FromGrpcStatus(string? grpcStatus) => grpcStatus?.ToUpperInvariant() switch
    {
        "OK" => NpsStatusCodes.Ok,
        "INVALID_ARGUMENT" => NpsStatusCodes.ClientBadParam,
        "FAILED_PRECONDITION" => NpsStatusCodes.ClientUnprocessable,
        "NOT_FOUND" => NpsStatusCodes.ClientNotFound,
        "ALREADY_EXISTS" or "ABORTED" => NpsStatusCodes.ClientConflict,
        "UNAUTHENTICATED" => NpsStatusCodes.AuthUnauthenticated,
        "PERMISSION_DENIED" => NpsStatusCodes.AuthForbidden,
        "RESOURCE_EXHAUSTED" => NpsStatusCodes.LimitRate,
        "UNIMPLEMENTED" => NpsStatusCodes.ServerUnsupported,
        "UNAVAILABLE" => NpsStatusCodes.ServerUnavailable,
        "DEADLINE_EXCEEDED" => NpsStatusCodes.ServerTimeout,
        "INTERNAL" or "UNKNOWN" or "DATA_LOSS" => NpsStatusCodes.ServerInternal,
        _ => NpsStatusCodes.ServerInternal,
    };

    /// <summary>
    /// Whether a failure MUST be surfaced as a protocol-level error rather than a successful
    /// response carrying an error payload.
    ///
    /// <para>
    /// §16.3 is explicit for auth: an <c>NPS-AUTH-*</c> failure "MUST be a JSON-RPC error, never
    /// a successful result carrying an error payload". Both pre-CR-0010 inbound implementations
    /// violated this — the SDK's <c>McpServerBridge</c> and <c>compat</c>'s <c>tools/call</c> both
    /// returned upstream failures as a successful result with <c>isError: true</c>, which lets an
    /// MCP client mistake a 403 for a tool that merely returned unhappy text.
    /// </para>
    /// <para>
    /// The set is <b>infrastructure failures</b>: auth, limit, unsupported, and the server /
    /// downstream / timeout classes. These mean the tool did not run — an MCP client must see a
    /// protocol error, not a tool that "returned" an error string. Genuine tool-domain failures
    /// (a <c>NPS-CLIENT-*</c> validation/conflict result the node deliberately produced) stay as
    /// <c>isError: true</c> content, which is what that flag is for. Including the server classes
    /// also keeps the §16.3 row <c>NPS-SERVER-* → -32603</c> live on the tools/call path rather
    /// than dead. (NPS-CR-0010 review finding F4)
    /// </para>
    /// </summary>
    public static bool MustBeProtocolError(string? npsStatus) => npsStatus switch
    {
        NpsStatusCodes.AuthUnauthenticated
            or NpsStatusCodes.AuthForbidden
            or NpsStatusCodes.LimitRate
            or NpsStatusCodes.LimitBudget
            or NpsStatusCodes.LimitPayload
            or NpsStatusCodes.ServerUnsupported
            or NpsStatusCodes.ServerInternal
            or NpsStatusCodes.ServerUnavailable
            or NpsStatusCodes.ServerTimeout
            or NpsStatusCodes.DownstreamUnavailable => true,
        _ => false,
    };
}
