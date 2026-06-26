// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

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
    /// <summary>
    /// Liveness probe. Always returns 200 unless the host is unable to run
    /// the route handler at all, in which case Kestrel won't accept the
    /// request and the orchestrator should restart the pod anyway.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthz(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () =>
        {
            var response = HealthProbeRenderer.RenderHealthz();
            return Results.Text(response.Body, response.ContentType, statusCode: response.StatusCode);
        });
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
            var response = await HealthProbeRenderer.RenderReadyzAsync(probes, ctx.RequestAborted);
            return Results.Text(response.Body, response.ContentType, statusCode: response.StatusCode);
        });
        return app;
    }
}
