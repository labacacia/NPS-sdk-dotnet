// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NPS.Daemon.Observability.Shutdown;

/// <summary>
/// Wires the daemon for graceful SIGTERM handling. Kestrel's default shutdown
/// timeout is 30s but on Linux <c>WebApplication.Run</c> already
/// catches SIGTERM and SIGINT — what we add here is:
///
/// <list type="number">
///   <item>An explicit 30s shutdown timeout on <see cref="HostOptions"/> so
///   the drain window is identical across daemons.</item>
///   <item>A liveness gate (<see cref="ShutdownState"/>) so <c>/healthz</c>
///   can fail early if the host is mid-drain, letting load balancers stop
///   routing before the listener actually closes.</item>
///   <item>An info-level log line on SIGTERM so the audit trail shows when
///   the signal arrived versus when the listeners closed.</item>
/// </list>
/// </summary>
public static class GracefulShutdown
{
    /// <summary>Default drain timeout for NPS daemons (NPS-Dev #45).</summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Registers shutdown timeout + liveness gate. Call on <c>builder.Services</c>.</summary>
    public static IServiceCollection AddGracefulShutdown(
        this IServiceCollection services,
        TimeSpan? drainTimeout = null)
    {
        var timeout = drainTimeout ?? DefaultDrainTimeout;
        services.Configure<HostOptions>(opts =>
        {
            opts.ShutdownTimeout                  = timeout;
            opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });
        services.AddSingleton<ShutdownState>();
        services.AddHostedService<ShutdownLogger>();
        return services;
    }

    /// <summary>
    /// Hooks the application lifetime so <c>/healthz</c> starts returning 503
    /// the moment SIGTERM arrives. Optional but recommended for orchestrated
    /// rollouts where the LB removes endpoints based on a failing probe.
    /// </summary>
    public static WebApplication UseShutdownLivenessGate(this WebApplication app)
    {
        var state    = app.Services.GetRequiredService<ShutdownState>();
        var lifetime = app.Lifetime;
        lifetime.ApplicationStopping.Register(state.MarkStopping);
        return app;
    }
}

/// <summary>Liveness flag flipped on SIGTERM; read by health probes.</summary>
public sealed class ShutdownState
{
    private int _stopping;
    public bool IsStopping => Volatile.Read(ref _stopping) == 1;
    public void MarkStopping() => Interlocked.Exchange(ref _stopping, 1);
}

internal sealed class ShutdownLogger : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ShutdownLogger>  _log;

    public ShutdownLogger(IHostApplicationLifetime lifetime, ILogger<ShutdownLogger> log)
    {
        _lifetime = lifetime;
        _log      = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _lifetime.ApplicationStopping.Register(() =>
            _log.LogInformation(
                "shutdown signal received; draining for up to {Timeout}s",
                (int)GracefulShutdown.DefaultDrainTimeout.TotalSeconds));
        _lifetime.ApplicationStopped.Register(() =>
            _log.LogInformation("shutdown complete"));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
