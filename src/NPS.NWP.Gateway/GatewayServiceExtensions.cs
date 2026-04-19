// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NPS.NOP.Orchestration;

namespace NPS.NWP.Gateway;

/// <summary>DI registration extensions for <see cref="GatewayNodeMiddleware"/>.</summary>
public static class GatewayServiceExtensions
{
    /// <summary>
    /// Registers a Gateway Node backed by <typeparamref name="TRouter"/>
    /// (NPS-AaaS §2). Requires a previously registered
    /// <see cref="INopOrchestrator"/> — call <c>services.AddNopOrchestrator(...)</c>
    /// (or equivalent) before this.
    ///
    /// <para>The default <see cref="IGatewayRateLimiter"/> is
    /// <see cref="InMemoryGatewayRateLimiter"/>; register your own
    /// (e.g. Redis-backed) before this call to override.</para>
    /// </summary>
    public static IServiceCollection AddGatewayNode<TRouter>(
        this IServiceCollection       services,
        Action<GatewayNodeOptions>    configure)
        where TRouter : class, IGatewayRouter
    {
        var opts = BuildOptions(configure);
        services.AddSingleton(opts);
        services.AddSingleton<IGatewayRouter, TRouter>();
        services.TryAddSingleton<IGatewayRateLimiter, InMemoryGatewayRateLimiter>();
        return services;
    }

    /// <summary>
    /// Wires up the <see cref="GatewayNodeMiddleware"/> into the ASP.NET Core
    /// pipeline. Call after <c>AddGatewayNode&lt;TRouter&gt;()</c>.
    /// </summary>
    public static IApplicationBuilder UseGatewayNode(
        this IApplicationBuilder    app,
        Action<GatewayNodeOptions>  configure)
    {
        var opts = BuildOptions(configure);
        return app.Use(next => ctx =>
        {
            var router       = ctx.RequestServices.GetRequiredService<IGatewayRouter>();
            var orchestrator = ctx.RequestServices.GetRequiredService<INopOrchestrator>();
            var limiter      = ctx.RequestServices.GetRequiredService<IGatewayRateLimiter>();
            var logger       = ctx.RequestServices.GetRequiredService<ILogger<GatewayNodeMiddleware>>();
            var mw = new GatewayNodeMiddleware(next, opts, router, orchestrator, limiter, logger);
            return mw.InvokeAsync(ctx);
        });
    }

    private static GatewayNodeOptions BuildOptions(Action<GatewayNodeOptions> configure)
    {
        var opts = new GatewayNodeOptions
        {
            NodeId     = string.Empty,
            PathPrefix = string.Empty,
            Actions    = new Dictionary<string, GatewayActionSpec>(),
        };
        configure(opts);
        return opts;
    }
}
