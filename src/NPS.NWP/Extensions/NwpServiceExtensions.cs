// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NPS.Core.Frames;
using NPS.Core.Registry;
using NPS.NWP.ActionNode;
using NPS.NWP.ComplexNode;
using NPS.NWP.Frames;
using NPS.NWP.MemoryNode;

namespace NPS.NWP.Extensions;

/// <summary>Configuration options for the NPS.NWP layer.</summary>
public sealed class NwpOptions
{
    /// <summary>NWP port. Default 17433 (unified port, NPS-2 §3). Optional independent: 17434.</summary>
    public int Port { get; set; } = 17433;

    /// <summary>
    /// Default token budget applied when <c>X-NWP-Budget</c> is absent.
    /// 0 means no limit. Default: 0.
    /// </summary>
    public uint DefaultTokenBudget { get; set; } = 0;

    /// <summary>Maximum <c>X-NWP-Depth</c> value this node will honour. Default 5 (spec max).</summary>
    public uint MaxDepth { get; set; } = 5;

    /// <summary>
    /// Default query result page size when <c>QueryFrame.Limit</c> is absent.
    /// Default: 20. Maximum: 1000 (NPS-2 §5.1).
    /// </summary>
    public uint DefaultLimit { get; set; } = 20;
}

/// <summary>DI registration extensions for NPS.NWP.</summary>
public static class NwpServiceExtensions
{
    /// <summary>
    /// Registers NPS.NWP services and extends the <see cref="FrameRegistry"/> with
    /// NWP frame types (<c>QueryFrame</c>, <c>ActionFrame</c>).
    /// <para>
    /// Prerequisite: <c>AddNpsCore()</c> MUST be called before <c>AddNwp()</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddNwp(
        this IServiceCollection services,
        Action<NwpOptions>? configure = null)
    {
        var options = new NwpOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Extend the singleton FrameRegistry with NWP frame type mappings.
        // Replaces the default NCP-only registry with one that also knows Query and Action.
        services.AddSingleton<FrameRegistry>(_ =>
            new FrameRegistryBuilder()
                // NCP frames (replicated here so the registry is self-contained)
                .Register<NPS.Core.Frames.Ncp.AnchorFrame>(FrameType.Anchor)
                .Register<NPS.Core.Frames.Ncp.DiffFrame>  (FrameType.Diff)
                .Register<NPS.Core.Frames.Ncp.StreamFrame>(FrameType.Stream)
                .Register<NPS.Core.Frames.Ncp.CapsFrame>  (FrameType.Caps)
                .Register<NPS.Core.Frames.Ncp.ErrorFrame>(FrameType.Error)
                // NWP frames
                .Register<QueryFrame> (FrameType.Query)
                .Register<ActionFrame>(FrameType.Action)
                .Build());

        return services;
    }

    /// <summary>
    /// Registers a Memory Node backed by <typeparamref name="TProvider"/>.
    /// The provider is resolved from the DI container, so register it separately
    /// (e.g. <c>services.AddSingleton&lt;TProvider&gt;()</c>).
    /// </summary>
    /// <typeparam name="TProvider">Concrete <see cref="IMemoryNodeProvider"/> implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to configure <see cref="MemoryNodeOptions"/>.</param>
    public static IServiceCollection AddMemoryNode<TProvider>(
        this IServiceCollection services,
        Action<MemoryNodeOptions> configure)
        where TProvider : class, IMemoryNodeProvider
    {
        var opts = BuildOptions(configure);
        services.AddSingleton<TProvider>();
        services.AddSingleton<MemoryNodeMiddleware>(sp =>
            new MemoryNodeMiddleware(
                next:     _ => Task.CompletedTask,  // placeholder; replaced by UseMemoryNode()
                provider: sp.GetRequiredService<TProvider>(),
                options:  opts,
                logger:   sp.GetRequiredService<ILogger<MemoryNodeMiddleware>>()));
        services.AddSingleton(opts);
        return services;
    }

    /// <summary>
    /// Wires up a Memory Node middleware for <typeparamref name="TProvider"/> into the ASP.NET Core pipeline.
    /// Call this from <c>app.UseMemoryNode&lt;TProvider&gt;(configure)</c>.
    /// </summary>
    public static IApplicationBuilder UseMemoryNode<TProvider>(
        this IApplicationBuilder app,
        Action<MemoryNodeOptions> configure)
        where TProvider : class, IMemoryNodeProvider
    {
        var opts = BuildOptions(configure);
        return app.Use(next => ctx =>
        {
            var provider = ctx.RequestServices.GetRequiredService<TProvider>();
            var logger   = ctx.RequestServices.GetRequiredService<ILogger<MemoryNodeMiddleware>>();
            var mw       = new MemoryNodeMiddleware(next, provider, opts, logger);
            return mw.InvokeAsync(ctx);
        });
    }

    private static MemoryNodeOptions BuildOptions(Action<MemoryNodeOptions> configure)
    {
        var opts = new MemoryNodeOptions
        {
            NodeId     = string.Empty,
            Schema     = null!,
            PathPrefix = string.Empty,
        };
        configure(opts);
        return opts;
    }

    // ── Action Node ──────────────────────────────────────────────────────────

    /// <summary>
    /// Registers an Action Node backed by <typeparamref name="TProvider"/> (NPS-2 §7).
    /// Also registers the in-memory task store and idempotency cache — replace them with
    /// distributed implementations for multi-instance deployments.
    /// </summary>
    public static IServiceCollection AddActionNode<TProvider>(
        this IServiceCollection services,
        Action<ActionNodeOptions> configure)
        where TProvider : class, IActionNodeProvider
    {
        var opts = BuildActionOptions(configure);
        services.AddSingleton(opts);
        services.AddSingleton<TProvider>();
        services.TryAddActionNodeInfrastructure();
        return services;
    }

    /// <summary>
    /// Wires up the Action Node middleware for <typeparamref name="TProvider"/> into the
    /// ASP.NET Core pipeline. Call after <c>AddActionNode&lt;TProvider&gt;()</c>.
    /// </summary>
    public static IApplicationBuilder UseActionNode<TProvider>(
        this IApplicationBuilder app,
        Action<ActionNodeOptions> configure)
        where TProvider : class, IActionNodeProvider
    {
        var opts = BuildActionOptions(configure);
        return app.Use(next => ctx =>
        {
            var provider    = ctx.RequestServices.GetRequiredService<TProvider>();
            var taskStore   = ctx.RequestServices.GetRequiredService<IActionTaskStore>();
            var idempotency = ctx.RequestServices.GetRequiredService<IIdempotencyCache>();
            var logger      = ctx.RequestServices.GetRequiredService<ILogger<ActionNodeMiddleware>>();
            var mw = new ActionNodeMiddleware(next, provider, opts, taskStore, idempotency, logger);
            return mw.InvokeAsync(ctx);
        });
    }

    private static ActionNodeOptions BuildActionOptions(Action<ActionNodeOptions> configure)
    {
        var opts = new ActionNodeOptions
        {
            NodeId     = string.Empty,
            Actions    = new Dictionary<string, ActionSpec>(),
            PathPrefix = string.Empty,
        };
        configure(opts);
        return opts;
    }

    private static void TryAddActionNodeInfrastructure(this IServiceCollection services)
    {
        // Register default in-memory stores only if the application hasn't already
        // supplied a custom implementation (e.g. Redis-backed).
        if (!services.Any(d => d.ServiceType == typeof(IActionTaskStore)))
            services.AddSingleton<IActionTaskStore, InMemoryActionTaskStore>();
        if (!services.Any(d => d.ServiceType == typeof(IIdempotencyCache)))
            services.AddSingleton<IIdempotencyCache, InMemoryIdempotencyCache>();
    }

    // ── Complex Node ─────────────────────────────────────────────────────────

    /// <summary>Named HTTP client used by the Complex Node for outbound child-node fetches.</summary>
    public const string ComplexNodeHttpClientName = "nwp-complex-child";

    /// <summary>
    /// Registers a Complex Node backed by <typeparamref name="TProvider"/> (NPS-2 §11).
    /// Also registers a named <see cref="HttpClient"/> (<see cref="ComplexNodeHttpClientName"/>)
    /// used for outbound graph expansion.
    /// </summary>
    public static IServiceCollection AddComplexNode<TProvider>(
        this IServiceCollection services,
        Action<ComplexNodeOptions> configure)
        where TProvider : class, IComplexNodeProvider
    {
        var opts = BuildComplexOptions(configure);
        services.AddSingleton(opts);
        services.AddSingleton<TProvider>();
        services.AddHttpClient(ComplexNodeHttpClientName);
        return services;
    }

    /// <summary>
    /// Wires up the Complex Node middleware for <typeparamref name="TProvider"/> into the
    /// ASP.NET Core pipeline. Call after <c>AddComplexNode&lt;TProvider&gt;()</c>.
    /// </summary>
    public static IApplicationBuilder UseComplexNode<TProvider>(
        this IApplicationBuilder app,
        Action<ComplexNodeOptions> configure)
        where TProvider : class, IComplexNodeProvider
    {
        var opts = BuildComplexOptions(configure);
        return app.Use(next => ctx =>
        {
            var provider = ctx.RequestServices.GetRequiredService<TProvider>();
            var httpFact = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();
            var logger   = ctx.RequestServices.GetRequiredService<ILogger<ComplexNodeMiddleware>>();
            var mw = new ComplexNodeMiddleware(next, provider, opts,
                httpFact.CreateClient(ComplexNodeHttpClientName), logger);
            return mw.InvokeAsync(ctx);
        });
    }

    private static ComplexNodeOptions BuildComplexOptions(Action<ComplexNodeOptions> configure)
    {
        var opts = new ComplexNodeOptions
        {
            NodeId     = string.Empty,
            PathPrefix = string.Empty,
        };
        configure(opts);
        return opts;
    }
}
