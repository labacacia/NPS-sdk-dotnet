// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NPS.Daemon.Observability.HealthChecks;
using NPS.Daemon.Observability.Metrics;
using NPS.Daemon.Observability.Shutdown;

namespace NPS.Daemon.Observability;

/// <summary>
/// Bootstrap shortcuts for NPS daemons.
/// </summary>
public static class ObservabilityServiceExtensions
{
    /// <summary>
    /// Registers the metrics registry + graceful-shutdown plumbing. Each
    /// daemon still owns its own <see cref="IReadinessProbe"/> registrations
    /// because those depend on daemon-specific services.
    /// </summary>
    public static IServiceCollection AddNpsObservability(
        this IServiceCollection services,
        TimeSpan? drainTimeout = null)
    {
        services.AddSingleton<MetricsRegistry>();
        services.AddGracefulShutdown(drainTimeout);
        return services;
    }

    /// <summary>
    /// Maps <c>/healthz</c>, <c>/readyz</c>, and <c>/metrics</c> in one call.
    /// </summary>
    public static IEndpointRouteBuilder MapNpsObservability(this IEndpointRouteBuilder app)
    {
        app.MapHealthz();
        app.MapReadyz();
        app.MapMetrics();
        return app;
    }

    /// <summary>Helper to register a readiness probe.</summary>
    public static IServiceCollection AddReadinessProbe(
        this IServiceCollection services, IReadinessProbe probe)
    {
        services.AddSingleton(probe);
        return services;
    }

    /// <summary>Helper to register a lambda-based readiness probe.</summary>
    public static IServiceCollection AddReadinessProbe(
        this IServiceCollection services, string name, Func<IServiceProvider, CancellationToken, Task<string?>> check)
    {
        services.AddSingleton<IReadinessProbe>(sp =>
            new DelegateReadinessProbe(name, ct => check(sp, ct)));
        return services;
    }
}
