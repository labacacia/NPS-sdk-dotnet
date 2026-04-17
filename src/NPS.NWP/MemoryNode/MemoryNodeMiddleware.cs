// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;
using NPS.NWP.Http;
using NPS.NWP.MemoryNode.Query;
using NPS.NWP.Nwm;

namespace NPS.NWP.MemoryNode;

/// <summary>
/// ASP.NET Core middleware that exposes a single Memory Node at a configurable path prefix.
/// Handles sub-paths: <c>/.nwm</c>, <c>/.schema</c>, <c>/query</c>, <c>/stream</c>.
/// </summary>
public sealed class MemoryNodeMiddleware
{
    private readonly RequestDelegate     _next;
    private readonly IMemoryNodeProvider _provider;
    private readonly MemoryNodeOptions   _options;
    private readonly ILogger             _logger;

    // Cached NWM JSON and anchor_id, computed once at startup.
    private readonly string   _nwmJson;
    private readonly string   _schemaJson;
    private readonly string   _anchorId;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented               = false,
    };

    public MemoryNodeMiddleware(
        RequestDelegate     next,
        IMemoryNodeProvider provider,
        MemoryNodeOptions   options,
        ILogger<MemoryNodeMiddleware> logger)
    {
        _next     = next;
        _provider = provider;
        _options  = options;
        _logger   = logger;

        (_nwmJson, _schemaJson, _anchorId) = BuildStaticPayloads(options);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path   = ctx.Request.Path.Value ?? string.Empty;
        var prefix = _options.PathPrefix.TrimEnd('/');

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        var sub = path[prefix.Length..];

        // Auth check (X-NWP-Agent required when configured)
        if (_options.RequireAuth && !ctx.Request.Headers.ContainsKey(NwpHttpHeaders.Agent))
        {
            await WriteError(ctx, 401, "NPS-CLIENT-UNAUTHORIZED", NwpErrorCodes.AuthNidScopeViolation,
                "X-NWP-Agent header is required.");
            return;
        }

        // Routing
        switch (sub)
        {
            case "/.nwm":
            case "/.nwm/":
                await HandleNwm(ctx);
                break;

            case "/.schema":
            case "/.schema/":
                await HandleSchema(ctx);
                break;

            case "/query":
            case "/query/":
                if (ctx.Request.Method != HttpMethods.Post)
                {
                    ctx.Response.StatusCode = 405;
                    return;
                }
                await HandleQuery(ctx);
                break;

            case "/stream":
            case "/stream/":
                if (ctx.Request.Method != HttpMethods.Post)
                {
                    ctx.Response.StatusCode = 405;
                    return;
                }
                await HandleStream(ctx);
                break;

            default:
                await _next(ctx);
                break;
        }
    }

    // ── Sub-path handlers ────────────────────────────────────────────────────

    private Task HandleNwm(HttpContext ctx)
    {
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeManifest;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "memory";
        return ctx.Response.WriteAsync(_nwmJson);
    }

    private Task HandleSchema(HttpContext ctx)
    {
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers[NwpHttpHeaders.Schema] = _anchorId;
        return ctx.Response.WriteAsync(_schemaJson);
    }

    private async Task HandleQuery(HttpContext ctx)
    {
        QueryFrame frame;
        try
        {
            frame = await ReadFrame<QueryFrame>(ctx);
        }
        catch (Exception ex)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST", NwpErrorCodes.QueryFilterInvalid, ex.Message);
            return;
        }

        // Budget
        var budget = ParseBudget(ctx);

        MemoryNodeQueryResult result;
        try
        {
            result = await _provider.QueryAsync(frame, _options.Schema, _options, ctx.RequestAborted);
        }
        catch (NwpFilterException ex)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST", ex.NwpErrorCode, ex.Message);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory Node query failed");
            await WriteError(ctx, 500, "NPS-SERVER-INTERNAL", "NWP-NODE-UNAVAILABLE", "Internal server error.");
            return;
        }

        // Serialize rows to JsonElement[]
        var dataElements = result.Rows
            .Select(row => JsonSerializer.SerializeToElement(row, s_json))
            .ToList();

        var tokenEst = NptMeter.MeasureRows(result.Rows, s_json);

        // Budget enforcement — trim rows if needed
        if (budget > 0 && tokenEst > budget)
        {
            dataElements = TrimToBudget(result.Rows, budget, out tokenEst);
        }

        var caps = new CapsFrame
        {
            AnchorRef  = _anchorId,
            Count      = (uint)dataElements.Count,
            Data       = dataElements,
            NextCursor = result.NextCursor,
            TokenEst   = tokenEst,
        };

        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeCapsule;
        ctx.Response.Headers[NwpHttpHeaders.Schema]  = _anchorId;
        ctx.Response.Headers[NwpHttpHeaders.Tokens]  = tokenEst.ToString();
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "memory";

        await ctx.Response.WriteAsync(JsonSerializer.Serialize(caps, s_json));
    }

    private async Task HandleStream(HttpContext ctx)
    {
        QueryFrame frame;
        try
        {
            frame = await ReadFrame<QueryFrame>(ctx);
        }
        catch (Exception ex)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST", NwpErrorCodes.QueryFilterInvalid, ex.Message);
            return;
        }

        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeCapsule;
        ctx.Response.Headers[NwpHttpHeaders.Schema]   = _anchorId;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "memory";

        uint seq     = 0;
        var streamId = Guid.NewGuid().ToString("N");

        try
        {
            await foreach (var page in _provider.StreamAsync(frame, _options.Schema, _options, ctx.RequestAborted))
            {
                var elements = page.Select(r => JsonSerializer.SerializeToElement(r, s_json)).ToList();

                var chunk = new StreamFrame
                {
                    StreamId  = streamId,
                    Seq       = seq++,
                    IsLast    = false,
                    AnchorRef = seq == 1 ? _anchorId : null,
                    Data      = elements,
                };
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(chunk, s_json) + "\n");
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            // Final empty sentinel chunk
            var final = new StreamFrame
            {
                StreamId = streamId,
                Seq      = seq,
                IsLast   = true,
                Data     = Array.Empty<JsonElement>(),
            };
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(final, s_json) + "\n");
        }
        catch (NwpFilterException ex)
        {
            _logger.LogWarning("Stream filter error: {Msg}", ex.Message);
            var errChunk = new StreamFrame
            {
                StreamId  = streamId,
                Seq       = seq,
                IsLast    = true,
                ErrorCode = ex.NwpErrorCode,
                Data      = Array.Empty<JsonElement>(),
            };
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(errChunk, s_json) + "\n");
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory Node stream failed");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<T> ReadFrame<T>(HttpContext ctx)
    {
        using var ms  = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ms.Position = 0;
        return JsonSerializer.Deserialize<T>(ms.ToArray(), s_json)
            ?? throw new InvalidOperationException("Failed to deserialize frame.");
    }

    private static uint ParseBudget(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue(NwpHttpHeaders.Budget, out var raw)
            && uint.TryParse(raw, out var b))
            return b;
        return 0;
    }

    private static List<JsonElement> TrimToBudget(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        uint budget,
        out uint totalTokens)
    {
        var result = new List<JsonElement>();
        uint acc   = 0;
        foreach (var row in rows)
        {
            var el  = JsonSerializer.SerializeToElement(row, s_json);
            var tok = NptMeter.MeasureJson(row, s_json);
            if (acc + tok > budget) break;
            result.Add(el);
            acc += tok;
        }
        totalTokens = acc;
        return result;
    }

    private static Task WriteError(HttpContext ctx, int status, string npsStatus, string errorCode, string message)
    {
        var err = new ErrorFrame
        {
            Status  = npsStatus,
            Error   = errorCode,
            Message = message,
        };
        ctx.Response.StatusCode  = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(err, s_json));
    }

    // ── Static payload builder ────────────────────────────────────────────────

    private static (string nwmJson, string schemaJson, string anchorId) BuildStaticPayloads(MemoryNodeOptions opt)
    {
        var baseUrl = opt.PathPrefix.TrimEnd('/');

        // Schema JSON (AnchorFrame schema)
        var frameSchema = new FrameSchema
        {
            Fields = opt.Schema.Fields
                .Select(f => new SchemaField(f.Name, f.Type, Nullable: f.Nullable))
                .ToList(),
        };
        var schemaJson = JsonSerializer.Serialize(frameSchema, s_json);

        // anchor_id = "sha256:" + hex(SHA256(canonical schema JSON))
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(schemaJson));
        var anchorId  = "sha256:" + Convert.ToHexString(hashBytes).ToLowerInvariant();

        // NWM
        var nwm = new NeuralWebManifest
        {
            Nwp             = "0.4",
            NodeId          = opt.NodeId,
            NodeType        = "memory",
            DisplayName     = opt.DisplayName,
            WireFormats     = ["ncp-capsule", "json"],
            PreferredFormat = "json",
            SchemaAnchors   = new Dictionary<string, string> { ["default"] = anchorId },
            Capabilities    = new NodeCapabilities
            {
                Query           = true,
                Stream          = true,
                TokenBudgetHint = true,
            },
            Auth = new NodeAuth
            {
                Required = opt.RequireAuth,
                IdentityType = opt.RequireAuth ? "nip-cert" : "none",
            },
            Endpoints = new NodeEndpoints
            {
                Query  = $"{baseUrl}/query",
                Stream = $"{baseUrl}/stream",
                Schema = $"{baseUrl}/.schema",
            },
        };
        var nwmJson = JsonSerializer.Serialize(nwm, s_json);

        return (nwmJson, schemaJson, anchorId);
    }
}
