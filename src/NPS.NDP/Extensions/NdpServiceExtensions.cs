// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using NPS.NDP.Registry;
using NPS.NDP.Validation;

namespace NPS.NDP.Extensions;

/// <summary>
/// DI registration extensions for the NDP library.
/// </summary>
public static class NdpServiceExtensions
{
    /// <summary>
    /// Registers the NDP in-memory node registry (<see cref="INdpRegistry"/>) and
    /// the announce validator (<see cref="NdpAnnounceValidator"/>) into the DI container.
    ///
    /// <para>Both are registered as singletons: the registry holds the live node table
    /// and the validator holds the known-public-key set for announce verification.</para>
    /// </summary>
    public static IServiceCollection AddNdp(this IServiceCollection services)
    {
        services.AddSingleton<INdpRegistry, InMemoryNdpRegistry>();
        services.AddSingleton<NdpAnnounceValidator>();
        return services;
    }
}
