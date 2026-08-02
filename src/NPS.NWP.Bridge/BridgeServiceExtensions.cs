// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace NPS.NWP.Bridge;

/// <summary>DI and ASP.NET pipeline extensions for Bridge hosting.</summary>
public static class BridgeServiceExtensions
{
    /// <summary>Named <see cref="HttpClient"/> used by built-in Bridge dispatchers.</summary>
    public const string HttpClientName = "nps-bridge";

    /// <summary>Register Bridge Node services with the built-in dispatchers enabled by default.</summary>
    public static IServiceCollection AddBridgeNode(
        this IServiceCollection services,
        Action<BridgeNodeOptions>? configure = null,
        Action<BridgeDispatcherRegistry>? configureDispatchers = null)
    {
        var options = new BridgeNodeOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddHttpClient(HttpClientName);

        services.AddSingleton(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            var registry = options.RegisterBuiltInDispatchers
                ? BridgeDispatcherRegistry.CreateDefault(httpClient)
                : new BridgeDispatcherRegistry();
            configureDispatchers?.Invoke(registry);
            return registry;
        });
        services.AddSingleton<BridgeNode>();
        return services;
    }

    /// <summary>Attach the Bridge Node middleware to the ASP.NET pipeline.</summary>
    public static IApplicationBuilder UseBridgeNode(this IApplicationBuilder app) =>
        app.Use(next => ctx =>
        {
            var bridge = ctx.RequestServices.GetRequiredService<BridgeNode>();
            var registry = ctx.RequestServices.GetRequiredService<BridgeDispatcherRegistry>();
            var options = ctx.RequestServices.GetRequiredService<BridgeNodeOptions>();
            var logger = ctx.RequestServices.GetRequiredService<ILogger<BridgeNodeMiddleware>>();
            var middleware = new BridgeNodeMiddleware(next, bridge, registry, options, logger);
            return middleware.InvokeAsync(ctx);
        });

    /// <summary>Register inbound Bridge server services for MCP and A2A clients.</summary>
    public static IServiceCollection AddBridgeServer(
        this IServiceCollection services,
        Action<BridgeServerOptions>? configure = null)
    {
        var options = new BridgeServerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        // The inbound servers depend on the transport-independent base type; register both so a
        // consumer can resolve either the hosting options or just the protocol surface.
        services.AddSingleton<BridgeInboundOptions>(options);
        services.AddHttpClient();
        services.TryAddSingleton<IReadOnlyList<INwpBackend>>(sp =>
            BridgeServerBackends.Create(
                sp.GetRequiredService<BridgeServerOptions>(),
                sp.GetService<IHttpClientFactory>()?.CreateClient(nameof(HttpNwpBackend))));
        services.AddSingleton<McpInboundServer>();
        services.AddSingleton<A2aInboundServer>();
        services.AddSingleton<GrpcInboundService>();
        return services;
    }

    /// <summary>Attach inbound MCP/A2A Bridge server middleware to the ASP.NET pipeline.</summary>
    public static IApplicationBuilder UseBridgeServer(this IApplicationBuilder app) =>
        app.Use(next => ctx =>
        {
            var mcp = ctx.RequestServices.GetRequiredService<McpInboundServer>();
            var a2a = ctx.RequestServices.GetRequiredService<A2aInboundServer>();
            var options = ctx.RequestServices.GetRequiredService<BridgeServerOptions>();
            var logger = ctx.RequestServices.GetRequiredService<ILogger<BridgeServerMiddleware>>();
            var middleware = new BridgeServerMiddleware(next, mcp, a2a, options, logger);
            return middleware.InvokeAsync(ctx);
        });
}
