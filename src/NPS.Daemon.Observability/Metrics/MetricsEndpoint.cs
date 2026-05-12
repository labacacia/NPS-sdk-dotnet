// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace NPS.Daemon.Observability.Metrics;

/// <summary>
/// Maps <c>GET /metrics</c> onto the host. The route resolves a singleton
/// <see cref="MetricsRegistry"/> from DI and writes its current snapshot in
/// the Prometheus/OpenMetrics text exposition format
/// (<c>text/plain; version=0.0.4; charset=utf-8</c>).
/// </summary>
public static class MetricsEndpoint
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static IEndpointRouteBuilder MapMetrics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics", (HttpContext ctx) =>
        {
            var reg = ctx.RequestServices.GetService<MetricsRegistry>();
            var sb  = new StringBuilder(1024);
            reg?.WriteTo(sb);
            return Results.Text(sb.ToString(), ContentType);
        });
        return app;
    }
}
