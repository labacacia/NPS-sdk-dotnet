// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0
//
// Deprecation stubs for the v1.0-alpha.2 NPS.NWP.Gateway public surface.
// Per NPS-CR-0001 the package and types were renamed Gateway -> Anchor in
// v1.0-alpha.3. These stubs throw on instantiation so callers updating
// from alpha.2 get an immediate, traceable failure pointing at the CR.
// This file is intended to live for ONE alpha release window only and
// will be deleted in v1.0-alpha.4.

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace NPS.NWP.Gateway;

/// <summary>
/// Removed in v1.0-alpha.3 by NPS-CR-0001. Use
/// <see cref="NPS.NWP.Anchor.AnchorNodeMiddleware"/>.
/// </summary>
[Obsolete(
    "GatewayNodeMiddleware was removed in v1.0-alpha.3. Use NPS.NWP.Anchor.AnchorNodeMiddleware. " +
    "See spec/cr/NPS-CR-0001-anchor-bridge-split.md for migration notes. " +
    "This stub throws on instantiation; it is removed in v1.0-alpha.4.",
    error: true)]
public sealed class GatewayNodeMiddleware
{
    public GatewayNodeMiddleware()
    {
        throw new InvalidOperationException(
            "GatewayNodeMiddleware was removed in v1.0-alpha.3 by NPS-CR-0001. " +
            "Replace 'using NPS.NWP.Gateway;' with 'using NPS.NWP.Anchor;' and " +
            "the type with NPS.NWP.Anchor.AnchorNodeMiddleware. " +
            "See spec/cr/NPS-CR-0001-anchor-bridge-split.md.");
    }
}

/// <summary>
/// Removed in v1.0-alpha.3 by NPS-CR-0001. Use
/// <see cref="NPS.NWP.Anchor.AnchorNodeOptions"/>.
/// </summary>
[Obsolete(
    "GatewayNodeOptions was removed in v1.0-alpha.3. Use NPS.NWP.Anchor.AnchorNodeOptions. " +
    "See spec/cr/NPS-CR-0001-anchor-bridge-split.md.",
    error: true)]
public sealed class GatewayNodeOptions { }

/// <summary>
/// Removed in v1.0-alpha.3 by NPS-CR-0001. Use
/// <see cref="NPS.NWP.Anchor.IAnchorRouter"/>.
/// </summary>
[Obsolete(
    "IGatewayRouter was removed in v1.0-alpha.3. Use NPS.NWP.Anchor.IAnchorRouter. " +
    "See spec/cr/NPS-CR-0001-anchor-bridge-split.md.",
    error: true)]
public interface IGatewayRouter { }

/// <summary>
/// Removed in v1.0-alpha.3 by NPS-CR-0001. Use
/// <see cref="NPS.NWP.Anchor.IAnchorRateLimiter"/>.
/// </summary>
[Obsolete(
    "IGatewayRateLimiter was removed in v1.0-alpha.3. Use NPS.NWP.Anchor.IAnchorRateLimiter. " +
    "See spec/cr/NPS-CR-0001-anchor-bridge-split.md.",
    error: true)]
public interface IGatewayRateLimiter { }

/// <summary>
/// Static-extension shim for <c>app.UseGatewayNode(...)</c>. Removed in
/// v1.0-alpha.3 by NPS-CR-0001 — switch to
/// <c>app.UseAnchorNode(...)</c>.
/// </summary>
public static class LegacyGatewayServiceExtensions
{
    [Obsolete(
        "UseGatewayNode / AddGatewayNode were removed in v1.0-alpha.3. " +
        "Replace with UseAnchorNode / AddAnchorNode (NPS.NWP.Anchor). " +
        "See spec/cr/NPS-CR-0001-anchor-bridge-split.md.",
        error: true)]
    public static IApplicationBuilder UseGatewayNode(this IApplicationBuilder app)
        => throw new InvalidOperationException(
            "UseGatewayNode was removed in v1.0-alpha.3 by NPS-CR-0001. " +
            "Use NPS.NWP.Anchor.AnchorServiceExtensions.UseAnchorNode instead. " +
            "See spec/cr/NPS-CR-0001-anchor-bridge-split.md.");

    [Obsolete(
        "UseGatewayNode / AddGatewayNode were removed in v1.0-alpha.3. " +
        "Replace with UseAnchorNode / AddAnchorNode (NPS.NWP.Anchor). " +
        "See spec/cr/NPS-CR-0001-anchor-bridge-split.md.",
        error: true)]
    public static IServiceCollection AddGatewayNode(this IServiceCollection services)
        => throw new InvalidOperationException(
            "AddGatewayNode was removed in v1.0-alpha.3 by NPS-CR-0001. " +
            "Use NPS.NWP.Anchor.AnchorServiceExtensions.AddAnchorNode instead. " +
            "See spec/cr/NPS-CR-0001-anchor-bridge-split.md.");
}
