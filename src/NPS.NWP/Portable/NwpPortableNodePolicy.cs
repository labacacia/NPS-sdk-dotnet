// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core;
using NPS.NWP.Http;

namespace NPS.NWP.Portable;

/// <summary>Transport used to present a portable NWP Node server.</summary>
public enum NwpServerTransport
{
    /// <summary>HTTP overlay mode.</summary>
    Http,

    /// <summary>Native NCP session mode.</summary>
    Native,
}

/// <summary>Operative role exposed by a portable NWP Node server.</summary>
public enum NwpPortableNodeRole
{
    /// <summary>Memory Node.</summary>
    Memory,

    /// <summary>Action Node.</summary>
    Action,

    /// <summary>Complex Node.</summary>
    Complex,
}

/// <summary>Terminal decision made by the portable server admission policy.</summary>
public enum NwpServerDecisionKind
{
    /// <summary>Serve the Neural Web Manifest.</summary>
    ServeManifest,

    /// <summary>Dispatch a QueryFrame.</summary>
    DispatchQuery,

    /// <summary>Dispatch an ActionFrame.</summary>
    DispatchAction,

    /// <summary>Reject with an HTTP response.</summary>
    Reject,

    /// <summary>Abort without synthesizing a response.</summary>
    Abort,

    /// <summary>Return a native ErrorFrame.</summary>
    ErrorFrame,
}

/// <summary>Input to the transport-independent Node admission policy.</summary>
public sealed record NwpPortableNodeRequest
{
    /// <summary>Serving transport.</summary>
    public required NwpServerTransport Transport { get; init; }

    /// <summary>Operative Node role.</summary>
    public required NwpPortableNodeRole NodeRole { get; init; }

    /// <summary>HTTP method; ignored in native mode.</summary>
    public string? Method { get; init; }

    /// <summary>HTTP sub-path; ignored in native mode.</summary>
    public string? Path { get; init; }

    /// <summary>HTTP request media type.</summary>
    public string? ContentType { get; init; }

    /// <summary>HTTP Accept header.</summary>
    public string? Accept { get; init; }

    /// <summary>Observed body size.</summary>
    public long BodyBytes { get; init; }

    /// <summary>Configured body limit.</summary>
    public long MaxBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>Decoded or expected frame kind: query, action, or another frame name.</summary>
    public string? FrameKind { get; init; }

    /// <summary>Whether frame decoding succeeded.</summary>
    public bool BodyValid { get; init; } = true;

    /// <summary>Whether caller cancellation was observed before response commit.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Transport-specific request correlation identity.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>Portable Node admission result.</summary>
public sealed record NwpPortableNodeDecision
{
    /// <summary>Terminal decision.</summary>
    public required NwpServerDecisionKind Decision { get; init; }

    /// <summary>HTTP status when a response is produced in HTTP mode.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Response media type.</summary>
    public string? ContentType { get; init; }

    /// <summary>NPS status code.</summary>
    public string? Status { get; init; }

    /// <summary>NWP protocol error code.</summary>
    public string? Error { get; init; }

    /// <summary>Value for an HTTP Allow header.</summary>
    public string? Allow { get; init; }

    /// <summary>Native response frame kind.</summary>
    public string? ResponseFrame { get; init; }

    /// <summary>Correlation identity copied from the request.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Terminal telemetry classification.</summary>
    public required string TelemetryOutcome { get; init; }

    /// <summary>Whether the deprecated alpha.17 request media type was admitted.</summary>
    public bool LegacyMediaTypeAccepted { get; init; }
}

/// <summary>
/// Pure admission policy shared by framework middleware and native Node hosts
/// for the NWP v0.20 portable server profile.
/// </summary>
public static class NwpPortableNodePolicy
{
    private const string NativeFrameUnsupported = "NWP-NATIVE-FRAME-UNSUPPORTED";

    /// <summary>Evaluate one request without reading a stream or invoking a provider.</summary>
    public static NwpPortableNodeDecision Evaluate(NwpPortableNodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Cancelled)
            return Decision(NwpServerDecisionKind.Abort, request, telemetry: "cancelled");

        return request.Transport == NwpServerTransport.Native
            ? EvaluateNative(request)
            : EvaluateHttp(request);
    }

    private static NwpPortableNodeDecision EvaluateHttp(NwpPortableNodeRequest request)
    {
        if (string.Equals(request.Path, "/.nwm", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                return MethodNotAllowed(request, "GET");

            return Decision(
                NwpServerDecisionKind.ServeManifest,
                request,
                httpStatus: 200,
                contentType: NwpHttpHeaders.MimeManifest);
        }

        var actionPath = string.Equals(request.Path, "/invoke", StringComparison.OrdinalIgnoreCase);
        var queryPath = string.Equals(request.Path, "/query", StringComparison.OrdinalIgnoreCase);
        if (!actionPath && !queryPath)
        {
            return Reject(
                request,
                404,
                NpsStatusCodes.ClientNotFound,
                NwpErrorCodes.HttpFrameBodyMalformed);
        }

        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return MethodNotAllowed(request, "POST");

        var mediaType = BaseMediaType(request.ContentType);
        var legacy = string.Equals(mediaType, NwpHttpHeaders.MimeLegacyFrame, StringComparison.OrdinalIgnoreCase);
        if (!legacy && !string.Equals(mediaType, NwpHttpHeaders.MimeFrame, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(
                request,
                400,
                NpsStatusCodes.ClientBadFrame,
                NwpErrorCodes.HttpContentTypeUnsupported);
        }

        if (!Accepts(request.Accept, NwpHttpHeaders.MimeCapsule))
        {
            return Reject(
                request,
                400,
                NpsStatusCodes.ClientBadParam,
                NwpErrorCodes.HttpAcceptUnsatisfiable);
        }

        if (request.MaxBodyBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxBodyBytes must be positive.");

        if (request.BodyBytes > request.MaxBodyBytes)
        {
            return Reject(
                request,
                413,
                NpsStatusCodes.LimitPayload,
                NwpErrorCodes.HttpBodyTooLarge);
        }

        if (!request.BodyValid)
        {
            return Reject(
                request,
                400,
                NpsStatusCodes.ClientBadFrame,
                NwpErrorCodes.HttpFrameBodyMalformed);
        }

        var canDispatch = queryPath
            ? request.NodeRole is NwpPortableNodeRole.Memory or NwpPortableNodeRole.Complex
            : request.NodeRole is NwpPortableNodeRole.Action or NwpPortableNodeRole.Complex;
        if (!canDispatch ||
            (queryPath && !IsFrame(request.FrameKind, "query")) ||
            (actionPath && !IsFrame(request.FrameKind, "action")))
        {
            return Reject(
                request,
                400,
                NpsStatusCodes.ClientBadFrame,
                NwpErrorCodes.HttpFrameBodyMalformed);
        }

        return Decision(
            queryPath ? NwpServerDecisionKind.DispatchQuery : NwpServerDecisionKind.DispatchAction,
            request,
            httpStatus: 200,
            contentType: NwpHttpHeaders.MimeCapsule,
            legacyMediaTypeAccepted: legacy);
    }

    private static NwpPortableNodeDecision EvaluateNative(NwpPortableNodeRequest request)
    {
        var query = IsFrame(request.FrameKind, "query") &&
                    request.NodeRole is NwpPortableNodeRole.Memory or NwpPortableNodeRole.Complex;
        var action = IsFrame(request.FrameKind, "action") &&
                     request.NodeRole is NwpPortableNodeRole.Action or NwpPortableNodeRole.Complex;

        if (request.BodyValid && (query || action))
        {
            return Decision(
                query ? NwpServerDecisionKind.DispatchQuery : NwpServerDecisionKind.DispatchAction,
                request,
                responseFrame: "caps");
        }

        return Decision(
            NwpServerDecisionKind.ErrorFrame,
            request,
            status: NpsStatusCodes.ClientBadFrame,
            error: NativeFrameUnsupported,
            responseFrame: "error",
            telemetry: "rejected");
    }

    private static NwpPortableNodeDecision MethodNotAllowed(
        NwpPortableNodeRequest request,
        string allowedMethod) =>
        Decision(
            NwpServerDecisionKind.Reject,
            request,
            httpStatus: 405,
            allow: allowedMethod,
            telemetry: "rejected");

    private static NwpPortableNodeDecision Reject(
        NwpPortableNodeRequest request,
        int httpStatus,
        string status,
        string error) =>
        Decision(
            NwpServerDecisionKind.Reject,
            request,
            httpStatus,
            NwpHttpHeaders.MimeError,
            status,
            error,
            telemetry: "rejected");

    private static NwpPortableNodeDecision Decision(
        NwpServerDecisionKind kind,
        NwpPortableNodeRequest request,
        int? httpStatus = null,
        string? contentType = null,
        string? status = null,
        string? error = null,
        string? allow = null,
        string? responseFrame = null,
        string telemetry = "success",
        bool legacyMediaTypeAccepted = false) =>
        new()
        {
            Decision = kind,
            HttpStatus = httpStatus,
            ContentType = contentType,
            Status = status,
            Error = error,
            Allow = allow,
            ResponseFrame = responseFrame,
            CorrelationId = request.CorrelationId,
            TelemetryOutcome = telemetry,
            LegacyMediaTypeAccepted = legacyMediaTypeAccepted,
        };

    private static bool IsFrame(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string? BaseMediaType(string? value) =>
        value?.Split(';', 2, StringSplitOptions.TrimEntries)[0];

    private static bool Accepts(string? accept, string responseType)
    {
        if (string.IsNullOrWhiteSpace(accept))
            return true;

        return accept.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(BaseMediaType)
            .Any(item =>
                string.Equals(item, "*/*", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item, "application/*", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item, responseType, StringComparison.OrdinalIgnoreCase));
    }
}
