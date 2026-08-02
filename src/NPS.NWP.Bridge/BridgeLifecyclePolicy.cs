// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core;

namespace NPS.NWP.Bridge;

/// <summary>Input to the NWP v0.20 outbound Bridge lifecycle policy.</summary>
public sealed record BridgeLifecycleRequest
{
    /// <summary>Requested Bridge protocol.</summary>
    public required string Protocol { get; init; }

    /// <summary>Requested upstream endpoint.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Protocols backed by a registered dispatcher.</summary>
    public required IReadOnlyCollection<string> RegisteredProtocols { get; init; }

    /// <summary>Whether plain HTTP endpoints are permitted.</summary>
    public bool AllowHttp { get; init; } = true;

    /// <summary>Whether private and loopback hosts are rejected.</summary>
    public bool RejectPrivate { get; init; } = true;

    /// <summary>Optional absolute endpoint prefixes.</summary>
    public IReadOnlyList<string> AllowedPrefixes { get; init; } = Array.Empty<string>();

    /// <summary>Total lifecycle deadline in milliseconds.</summary>
    public long TimeoutMs { get; init; }

    /// <summary>Elapsed lifecycle time in milliseconds.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>Whether caller cancellation has already been observed.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Correlation identity to propagate.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Action task mode: sync or async.</summary>
    public string TaskMode { get; init; } = "sync";
}

/// <summary>Terminal result of outbound Bridge preflight.</summary>
public sealed record BridgeLifecycleDecision
{
    /// <summary>dispatch, reject, or abort.</summary>
    public required string Decision { get; init; }

    /// <summary>HTTP status when a response is produced.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>NPS status code.</summary>
    public string? Status { get; init; }

    /// <summary>NWP Bridge error code.</summary>
    public string? Error { get; init; }

    /// <summary>Correlation identity copied from the request.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Resolved task mode.</summary>
    public string? TaskMode { get; init; }

    /// <summary>Terminal telemetry classification.</summary>
    public required string TelemetryOutcome { get; init; }
}

/// <summary>
/// Pure preflight policy for the NWP v0.20 portable outbound Bridge profile.
/// No upstream connection is made while evaluating this policy.
/// </summary>
public static class BridgeLifecyclePolicy
{
    /// <summary>Evaluate target shape, dispatcher, endpoint, cancellation, and deadline.</summary>
    public static BridgeLifecycleDecision Evaluate(BridgeLifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Cancelled)
            return Result(request, "abort", telemetry: "cancelled");

        if (string.IsNullOrWhiteSpace(request.Protocol) || string.IsNullOrWhiteSpace(request.Endpoint))
        {
            return Result(
                request,
                "reject",
                422,
                NpsStatusCodes.ClientUnprocessable,
                BridgeErrorCodes.TargetInvalid,
                "rejected");
        }

        if (!request.RegisteredProtocols.Contains(request.Protocol, StringComparer.OrdinalIgnoreCase))
        {
            return Result(
                request,
                "reject",
                501,
                NpsStatusCodes.ServerUnsupported,
                BridgeErrorCodes.ProtocolUnsupported,
                "rejected");
        }

        var extras = new Dictionary<string, object?>
        {
            ["allow_http"] = request.AllowHttp,
            ["reject_private"] = request.RejectPrivate,
        };
        if (request.AllowedPrefixes.Count > 0)
            extras["allowed_prefixes"] = request.AllowedPrefixes;

        try
        {
            BridgeEndpointValidator.ParseHttpEndpoint(
                new BridgeTarget(request.Protocol, request.Endpoint, extras));
        }
        catch (BridgeDispatchException ex)
        {
            return Result(
                request,
                "reject",
                422,
                NpsStatusCodes.ClientUnprocessable,
                ex.ErrorCode,
                "rejected");
        }

        if (request.TimeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "TimeoutMs must be positive.");
        if (request.ElapsedMs < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "ElapsedMs must not be negative.");

        if (request.ElapsedMs >= request.TimeoutMs)
        {
            return Result(
                request,
                "reject",
                504,
                NpsStatusCodes.ServerTimeout,
                BridgeErrorCodes.UpstreamFailed,
                "timeout");
        }

        var async = string.Equals(request.TaskMode, "async", StringComparison.OrdinalIgnoreCase);
        return Result(
            request,
            "dispatch",
            status: async ? NpsStatusCodes.OkAccepted : NpsStatusCodes.Ok,
            telemetry: "success",
            taskMode: async ? "async" : "sync");
    }

    private static BridgeLifecycleDecision Result(
        BridgeLifecycleRequest request,
        string decision,
        int? httpStatus = null,
        string? status = null,
        string? error = null,
        string telemetry = "success",
        string? taskMode = null) =>
        new()
        {
            Decision = decision,
            HttpStatus = httpStatus,
            Status = status,
            Error = error,
            CorrelationId = request.CorrelationId,
            TaskMode = taskMode,
            TelemetryOutcome = telemetry,
        };
}
