// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NPS.Core.Frames;
using NPS.Core.Registry;
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
}
