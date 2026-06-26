// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace NPS.Daemon.Observability.HealthChecks;

/// <summary>Transport-neutral health/readiness response.</summary>
public sealed record HealthProbeResponse(
    int StatusCode,
    string ContentType,
    string Body,
    string Status,
    string? Reason = null);

/// <summary>
/// Renders liveness and readiness probes without depending on ASP.NET routing.
/// HttpListener, custom TCP, and test hosts can write the returned status code,
/// content type, and body directly to their transport.
/// </summary>
public static class HealthProbeRenderer
{
    public const string JsonContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented        = false,
    };

    /// <summary>Renders the liveness response used by <c>/healthz</c>.</summary>
    public static HealthProbeResponse RenderHealthz() =>
        Ok();

    /// <summary>
    /// Runs the supplied probes and renders the readiness response used by <c>/readyz</c>.
    /// With no probes, readiness is <c>ok</c>.
    /// </summary>
    public static async Task<HealthProbeResponse> RenderReadyzAsync(
        IEnumerable<IReadinessProbe> probes,
        CancellationToken ct = default)
    {
        foreach (var probe in probes)
        {
            string? reason;
            try
            {
                reason = await probe.CheckAsync(ct);
            }
            catch (Exception ex)
            {
                reason = $"{probe.Name}: {ex.Message}";
            }

            if (reason is not null)
                return Error(reason);
        }

        return Ok();
    }

    private static HealthProbeResponse Ok()
    {
        var body = JsonSerializer.Serialize(new { status = "ok" }, s_json);
        return new HealthProbeResponse(200, JsonContentType, body, "ok");
    }

    private static HealthProbeResponse Error(string reason)
    {
        var body = JsonSerializer.Serialize(new { status = "error", reason }, s_json);
        return new HealthProbeResponse(503, JsonContentType, body, "error", reason);
    }
}
