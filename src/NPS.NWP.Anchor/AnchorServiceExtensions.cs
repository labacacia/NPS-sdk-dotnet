// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NPS.NOP.Orchestration;

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
