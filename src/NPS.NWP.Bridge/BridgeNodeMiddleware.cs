// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPS.Core;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;
using NPS.NWP.Http;

namespace NPS.NWP.Bridge;

/// <summary>
/// ASP.NET Core middleware exposing a Bridge Node at <c>/.nwm</c>,
/// <c>/actions</c>, and <c>/invoke</c>.
/// </summary>
public sealed class BridgeNodeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly BridgeNode _bridge;
    private readonly BridgeDispatcherRegistry _registry;
    private readonly BridgeNodeOptions _options;
    private readonly ILogger _logger;

    internal static JsonSerializerOptions Json => BridgeFrameJson.Json;

    /// <summary>Create Bridge Node middleware.</summary>
    public BridgeNodeMiddleware(
        RequestDelegate next,
        BridgeNode bridge,
        BridgeDispatcherRegistry registry,
        BridgeNodeOptions options,
        ILogger<BridgeNodeMiddleware> logger)
    {
        _next = next;
        _bridge = bridge;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    /// <summary>Handle one HTTP request.</summary>
    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        var prefix = _options.PathPrefix.TrimEnd('/');

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        var sub = path[prefix.Length..];
        switch (sub)
        {
            case "/.nwm":
            case "/.nwm/":
                await WriteJson(ctx, 200, BuildManifest(), NwpHttpHeaders.MimeManifest);
                break;

            case "/actions":
            case "/actions/":
                await WriteJson(ctx, 200, BuildActions());
                break;

            case "/invoke":
            case "/invoke/":
                if (ctx.Request.Method != HttpMethods.Post)
                {
                    ctx.Response.StatusCode = 405;
                    return;
                }

                await HandleInvoke(ctx);
                break;

            default:
                await _next(ctx);
                break;
        }
    }

    private async Task HandleInvoke(HttpContext ctx)
    {
        if (_options.RequireAuth && !ctx.Request.Headers.ContainsKey(NwpHttpHeaders.Agent))
        {
            await WriteError(ctx, 401, NpsStatusCodes.AuthUnauthenticated,
                "NWP-BRIDGE-AUTH-REQUIRED", "X-NWP-Agent header is required.");
            return;
        }

        ActionFrame? frame;
        try
        {
            frame = await JsonSerializer.DeserializeAsync<ActionFrame>(
                ctx.Request.Body, Json, ctx.RequestAborted);
        }
        catch (JsonException ex)
        {
            await WriteError(ctx, 400, NpsStatusCodes.ClientBadFrame,
                BridgeErrorCodes.TargetInvalid, ex.Message);
            return;
        }

        if (frame is null)
        {
            await WriteError(ctx, 422, NpsStatusCodes.ClientUnprocessable,
                BridgeErrorCodes.TargetInvalid, "ActionFrame body is required.");
            return;
        }

        if (!string.Equals(frame.ActionId, _options.ActionId, StringComparison.Ordinal))
        {
            await WriteError(ctx, 404, "NPS-CLIENT-NOT-FOUND",
                "NWP-BRIDGE-ACTION-NOT-FOUND", $"Unknown bridge action '{frame.ActionId}'.");
            return;
        }

        try
        {
            var caps = await _bridge.DispatchAsync(frame, ctx.RequestAborted);
            await WriteJson(ctx, 200, caps);
        }
        catch (BridgeDispatchException ex)
        {
            var status = ex.ErrorCode == BridgeErrorCodes.UpstreamFailed ? 502 : 422;
            var npsStatus = status == 502 ? NpsStatusCodes.DownstreamUnavailable : NpsStatusCodes.ClientUnprocessable;
            await WriteError(ctx, status, npsStatus, ex.ErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bridge Node dispatch failed.");
            await WriteError(ctx, 500, NpsStatusCodes.ServerInternal,
                BridgeErrorCodes.UpstreamFailed, ex.Message);
        }
    }

    private object BuildManifest() => new
    {
        node_type = BridgeNodeMetadata.NodeType,
        node_id = _options.NodeId,
        bridge_protocols = _registry.Protocols.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        actions = new[] { _options.ActionId },
    };

    private object BuildActions() => new[]
    {
        new
        {
            action_id = _options.ActionId,
            description = "Dispatch an NWP ActionFrame to an external Bridge target.",
            bridge_protocols = _registry.Protocols.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        },
    };

    private static Task WriteJson(HttpContext ctx, int status, object body, string contentType = "application/json")
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        return JsonSerializer.SerializeAsync(ctx.Response.Body, body, Json);
    }

    private static Task WriteError(
        HttpContext ctx,
        int httpStatus,
        string status,
        string error,
        string message)
    {
        var frame = new ErrorFrame
        {
            Status = status,
            Error = error,
            Message = message,
        };

        return WriteJson(ctx, httpStatus, frame);
    }
}
