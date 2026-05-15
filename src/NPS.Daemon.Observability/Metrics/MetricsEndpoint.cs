// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
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

    public static IEndpointRouteBuilder MapMetrics(
        this IEndpointRouteBuilder app,
        string? bearerToken = null,
        bool requireBearerToken = false)
    {
        app.MapGet("/metrics", (HttpContext ctx) =>
        {
            if (!Authorize(ctx, bearerToken, requireBearerToken, out var authFailure))
                return authFailure;

            var reg = ctx.RequestServices.GetService<MetricsRegistry>();
            var sb  = new StringBuilder(1024);
            reg?.WriteTo(sb);
            return Results.Text(sb.ToString(), ContentType);
        });
        return app;
    }

    private static bool Authorize(
        HttpContext ctx,
        string? bearerToken,
        bool requireBearerToken,
        out IResult authFailure)
    {
        authFailure = Results.Empty;

        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            if (!requireBearerToken)
                return true;

            authFailure = Results.NotFound();
            return false;
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            authFailure = Results.Unauthorized();
            return false;
        }

        var supplied = header[prefix.Length..];
        if (FixedTimeEquals(supplied, bearerToken))
            return true;

        authFailure = Results.Unauthorized();
        return false;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes  = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
