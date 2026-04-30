// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NPS.NOP.Orchestration;
using NPS.NWP.Anchor.Topology;

namespace NPS.NWP.Anchor;

/// <summary>DI registration extensions for <see cref="AnchorNodeMiddleware"/>.</summary>
public static class AnchorServiceExtensions
{
    /// <summary>
    /// Registers a Anchor Node backed by <typeparamref name="TRouter"/>
    /// (NPS-AaaS §2). Requires a previously registered
    /// <see cref="INopOrchestrator"/> — call <c>services.AddNopOrchestrator(...)</c>
    /// (or equivalent) before this.
    ///
    /// <para>The default <see cref="IAnchorRateLimiter"/> is
    /// <see cref="InMemoryAnchorRateLimiter"/>; register your own
    /// (e.g. Redis-backed) before this call to override.</para>
    ///
    /// <para>Topology read-back (<c>topology.snapshot</c> /
    /// <c>topology.stream</c>, NPS-2 §12) is optional; call
    /// <see cref="AddInMemoryAnchorTopology"/> after this to opt in to the
    /// reference implementation, or register a custom
    /// <see cref="IAnchorTopologyService"/> manually.</para>
    /// </summary>
    public static IServiceCollection AddAnchorNode<TRouter>(
        this IServiceCollection       services,
        Action<AnchorNodeOptions>    configure)
        where TRouter : class, IAnchorRouter
    {
        var opts = BuildOptions(configure);
        services.AddSingleton(opts);
        services.AddSingleton<IAnchorRouter, TRouter>();
        services.TryAddSingleton<IAnchorRateLimiter, InMemoryAnchorRateLimiter>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="InMemoryAnchorTopologyService"/> as the
    /// <see cref="IAnchorTopologyService"/> implementation, satisfying L2-08
    /// (NPS-AaaS-Profile §4.3) for single-node deployments. Production
    /// multi-node deployments SHOULD register a distributed-store-backed
    /// implementation instead.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="anchorNid">Identity surfaced in <see cref="TopologySnapshot.AnchorNid"/>.</param>
    /// <param name="retention">Ring buffer size for stream replay; default <see cref="InMemoryAnchorTopologyService.DefaultRetention"/>.</param>
    public static IServiceCollection AddInMemoryAnchorTopology(
        this IServiceCollection services,
        string                  anchorNid,
        int?                    retention = null)
    {
        var instance = retention is { } r
            ? new InMemoryAnchorTopologyService(anchorNid, r)
            : new InMemoryAnchorTopologyService(anchorNid);
        services.AddSingleton(instance);
        services.AddSingleton<IAnchorTopologyService>(instance);
        return services;
    }

    /// <summary>
    /// Wires up the <see cref="AnchorNodeMiddleware"/> into the ASP.NET Core
    /// pipeline. Call after <c>AddAnchorNode&lt;TRouter&gt;()</c>.
    /// </summary>
    public static IApplicationBuilder UseAnchorNode(
        this IApplicationBuilder    app,
        Action<AnchorNodeOptions>  configure)
    {
        var opts = BuildOptions(configure);
        return app.Use(next => ctx =>
        {
            var router       = ctx.RequestServices.GetRequiredService<IAnchorRouter>();
            var orchestrator = ctx.RequestServices.GetRequiredService<INopOrchestrator>();
            var limiter      = ctx.RequestServices.GetRequiredService<IAnchorRateLimiter>();
            var logger       = ctx.RequestServices.GetRequiredService<ILogger<AnchorNodeMiddleware>>();
            var mw = new AnchorNodeMiddleware(next, opts, router, orchestrator, limiter, logger);
            return mw.InvokeAsync(ctx);
        });
    }

    private static AnchorNodeOptions BuildOptions(Action<AnchorNodeOptions> configure)
    {
        var opts = new AnchorNodeOptions
        {
            NodeId     = string.Empty,
            PathPrefix = string.Empty,
            Actions    = new Dictionary<string, AnchorActionSpec>(),
        };
        configure(opts);
        return opts;
    }
}
