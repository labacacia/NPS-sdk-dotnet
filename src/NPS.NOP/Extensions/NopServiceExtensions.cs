// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using NPS.NOP.Orchestration;
using NPS.NOP.Storage;

namespace NPS.NOP.Extensions;

/// <summary>
/// DI registration extensions for NPS.NOP Orchestrator services.
/// </summary>
public static class NopServiceExtensions
{
    /// <summary>
    /// Registers the NOP Orchestrator into the DI container.
    /// <para>
    /// Caller MUST register an <see cref="INopWorkerClient"/> implementation before calling this.
    /// </para>
    /// <code>
    /// services.AddSingleton&lt;INopWorkerClient, MyHttpWorkerClient&gt;();
    /// services.AddNopOrchestrator(opts => opts.MaxConcurrentNodes = 4);
    /// </code>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="NopOrchestratorOptions"/>.</param>
    /// <param name="useInMemoryStore">
    /// When <c>true</c> (default), registers <see cref="InMemoryNopTaskStore"/>.
    /// Pass <c>false</c> and register a custom <see cref="INopTaskStore"/> before calling this.
    /// </param>
    public static IServiceCollection AddNopOrchestrator(
        this IServiceCollection services,
        Action<NopOrchestratorOptions>? configure     = null,
        bool                            useInMemoryStore = true)
    {
        var opts = new NopOrchestratorOptions();
        configure?.Invoke(opts);
        services.AddSingleton(opts);

        if (useInMemoryStore)
            services.AddSingleton<INopTaskStore, InMemoryNopTaskStore>();

        services.AddSingleton<INopOrchestrator, NopOrchestrator>();

        return services;
    }
}
