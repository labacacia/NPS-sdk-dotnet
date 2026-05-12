// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace NPS.Daemon.Observability.HealthChecks;

/// <summary>
/// One readiness probe — daemons register one per backing dependency
/// (storage, key material, etc.) and <c>/readyz</c> returns 503 if any
/// of them fails. Probes MUST be fast (no I/O timeouts longer than ~1 s);
/// callers wire CancellationToken from the request.
/// </summary>
public interface IReadinessProbe
{
    /// <summary>Short name used in the JSON response (e.g. <c>"storage"</c>).</summary>
    string Name { get; }

    /// <summary>Returns null on success, a short reason on failure.</summary>
    Task<string?> CheckAsync(CancellationToken ct);
}

/// <summary>
/// Minimal in-line readiness probe wrapper, useful for callers that want to
/// hand a lambda instead of authoring a new class per dependency.
/// </summary>
public sealed class DelegateReadinessProbe : IReadinessProbe
{
    private readonly Func<CancellationToken, Task<string?>> _check;
    public string Name { get; }
    public DelegateReadinessProbe(string name, Func<CancellationToken, Task<string?>> check)
    {
        Name   = name;
        _check = check;
    }
    public Task<string?> CheckAsync(CancellationToken ct) => _check(ct);
}

/// <summary>
/// Endpoint mappings for <c>/healthz</c> (liveness) and <c>/readyz</c>
/// (dependency readiness).
/// </summary>
public static class HealthEndpoints
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented        = false,
    };

    /// <summary>
    /// Liveness probe. Always returns 200 unless the host is unable to run
    /// the route handler at all, in which case Kestrel won't accept the
    /// request and the orchestrator should restart the pod anyway.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthz(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }, s_json));
        return app;
    }

    /// <summary>
    /// Readiness probe. Walks every registered <see cref="IReadinessProbe"/>
    /// and returns 503 with the first failing probe's reason. With no
    /// probes registered, behaves like <c>/healthz</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapReadyz(this IEndpointRouteBuilder app)
    {
        app.MapGet("/readyz", async (HttpContext ctx) =>
        {
            var probes = ctx.RequestServices.GetServices<IReadinessProbe>();
            foreach (var probe in probes)
            {
                string? reason;
                try
                {
                    reason = await probe.CheckAsync(ctx.RequestAborted);
                }
                catch (Exception ex)
                {
                    reason = $"{probe.Name}: {ex.Message}";
                }
                if (reason is not null)
                    return Results.Json(new { status = "error", reason }, s_json, statusCode: 503);
            }
            return Results.Json(new { status = "ok" }, s_json);
        });
        return app;
    }
}
