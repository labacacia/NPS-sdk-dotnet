// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;
using NPS.NWP.Http;
using NPS.NWP.MemoryNode;
using NPS.NWP.Nwm;

namespace NPS.NWP.ComplexNode;

/// <summary>
/// ASP.NET Core middleware exposing a single Complex Node at a configurable path prefix
/// (NPS-2 §2.1, §11). Sub-paths: <c>/.nwm</c>, <c>/.schema</c>, <c>/query</c>,
/// <c>/invoke</c>, <c>/actions</c>.
///
/// <para>
/// On <c>/query</c>, the middleware asks the <see cref="IComplexNodeProvider"/> for the
/// local rows, then — if the request carries <c>X-NWP-Depth &gt; 0</c> and at least one
/// child is declared — fetches each child's <c>/query</c> concurrently and attaches the
/// embedded CapsFrame bodies under the <c>graph</c> field of the outgoing CapsFrame's
/// <c>meta</c> (surfaced as an extra root property next to <c>data</c>).
/// </para>
///
/// <para>Security (NPS-2 §13.2):</para>
/// <list type="bullet">
///   <item>Child URLs are validated against the configured prefix allowlist.</item>
///   <item>Cycle detection uses the <c>X-NWP-Trace</c> header (comma-separated node_ids).</item>
///   <item>Depth is clamped to <see cref="ComplexNodeOptions.AbsoluteMaxDepth"/> (5).</item>
/// </list>
/// </summary>
public sealed class ComplexNodeMiddleware
{
    /// <summary>Header used to carry the visited-nodes trace for cycle detection.</summary>
    public const string TraceHeader = "X-NWP-Trace";

    private readonly RequestDelegate         _next;
    private readonly IComplexNodeProvider    _provider;
    private readonly ComplexNodeOptions      _options;
    private readonly HttpClient              _http;
    private readonly ILogger                 _log;

    private readonly string _nwmJson;
    private readonly string _schemaJson;
    private readonly string _actionsJson;
    private readonly string _anchorId;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = false,
    };

    public ComplexNodeMiddleware(
        RequestDelegate               next,
        IComplexNodeProvider          provider,
        ComplexNodeOptions            options,
        HttpClient                    http,
        ILogger<ComplexNodeMiddleware> logger)
    {
        _next     = next;
        _provider = provider;
        _options  = options;
        _http     = http;
        _log      = logger;

        if (_options.Actions.ContainsKey(ActionNodeMiddleware.SystemTaskStatus) ||
            _options.Actions.ContainsKey(ActionNodeMiddleware.SystemTaskCancel))
        {
            throw new InvalidOperationException(
                $"Reserved action ids '{ActionNodeMiddleware.SystemTaskStatus}' / " +
                $"'{ActionNodeMiddleware.SystemTaskCancel}' MUST NOT be registered on a Complex Node.");
        }

        if (_options.GraphMaxDepth > ComplexNodeOptions.AbsoluteMaxDepth)
        {
            throw new InvalidOperationException(
                $"GraphMaxDepth {_options.GraphMaxDepth} exceeds NPS-2 §11 absolute cap " +
                $"{ComplexNodeOptions.AbsoluteMaxDepth}.");
        }

        (_nwmJson, _schemaJson, _actionsJson, _anchorId) = BuildStaticPayloads(_options);
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

        if (_options.RequireAuth && !ctx.Request.Headers.ContainsKey(NwpHttpHeaders.Agent))
        {
            await WriteError(ctx, 401, "NPS-CLIENT-UNAUTHORIZED",
                NwpErrorCodes.AuthNidScopeViolation,
                "X-NWP-Agent header is required.");
            return;
        }

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

            case "/actions":
            case "/actions/":
                await HandleActions(ctx);
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

    // ── Static endpoints ─────────────────────────────────────────────────────

    private Task HandleNwm(HttpContext ctx)
    {
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeManifest;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "complex";
        return ctx.Response.WriteAsync(_nwmJson);
    }

    private Task HandleSchema(HttpContext ctx)
    {
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = "application/json";
        if (_anchorId.Length > 0)
            ctx.Response.Headers[NwpHttpHeaders.Schema] = _anchorId;
        return ctx.Response.WriteAsync(_schemaJson);
    }

    private Task HandleActions(HttpContext ctx)
    {
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(_actionsJson);
    }

    // ── /query ───────────────────────────────────────────────────────────────

    private async Task HandleQuery(HttpContext ctx)
    {
        QueryFrame frame;
        try
        {
            frame = await ReadFrame<QueryFrame>(ctx);
        }
        catch (Exception ex)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.QueryFilterInvalid, ex.Message);
            return;
        }

        // Depth parsing (header absent => 0 = local only)
        if (!TryParseDepth(ctx, out var requestedDepth, out var depthError))
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.DepthExceeded, depthError!);
            return;
        }

        // Cycle detection — if this node is already in the trace, abort before we
        // recurse into ourselves (e.g. A → B → A).
        var trace = ParseTrace(ctx);
        if (trace.Contains(_options.NodeId))
        {
            await WriteError(ctx, 422, "NPS-CLIENT-UNPROCESSABLE",
                NwpErrorCodes.GraphCycle,
                $"graph cycle detected at '{_options.NodeId}'.");
            return;
        }

        MemoryNodeQueryResult local;
        try
        {
            local = await _provider.QueryAsync(frame, _options, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Complex Node local query failed");
            await WriteError(ctx, 500, "NPS-SERVER-INTERNAL",
                NwpErrorCodes.NodeUnavailable, "local query failed.");
            return;
        }

        var localData = local.Rows
            .Select(r => JsonSerializer.SerializeToElement(r, Json))
            .ToList();

        // Graph expansion
        JsonElement? graphElement = null;
        if (requestedDepth > 0 && _options.Graph.Count > 0)
        {
            var nextTrace = trace.ToList();
            nextTrace.Add(_options.NodeId);

            var childDepth = requestedDepth - 1;
            var childTasks = _options.Graph.Select(r =>
                FetchChildAsync(r, frame, childDepth, nextTrace, ctx));

            var childResults = await Task.WhenAll(childTasks);

            // Emit one { rel, node, data?, error? } object per declared ref.
            var arr = new List<JsonElement>(childResults.Length);
            foreach (var cr in childResults)
                arr.Add(JsonSerializer.SerializeToElement(cr, Json));

            graphElement = JsonSerializer.SerializeToElement(arr, Json);
        }

        var caps = new ComplexCapsFrame
        {
            AnchorRef  = _anchorId.Length > 0 ? _anchorId : null,
            Count      = (uint)localData.Count,
            Data       = localData,
            NextCursor = local.NextCursor,
            Graph      = graphElement,
        };

        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeCapsule;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "complex";
        if (_anchorId.Length > 0)
            ctx.Response.Headers[NwpHttpHeaders.Schema] = _anchorId;

        await ctx.Response.WriteAsync(JsonSerializer.Serialize(caps, Json));
    }

    private async Task<ChildFetchResult> FetchChildAsync(
        ComplexGraphRef   gref,
        QueryFrame        frame,
        uint              childDepth,
        IReadOnlyList<string> nextTrace,
        HttpContext       parentCtx)
    {
        var ssrfError = ComplexChildUrlValidator.Validate(
            gref.NodeUrl, _options.AllowedChildUrlPrefixes,
            _options.RejectPrivateChildUrls, _options.AllowHttpChildUrls);
        if (ssrfError is not null)
        {
            return new ChildFetchResult
            {
                Rel   = gref.Rel,
                Node  = gref.NodeUrl,
                Error = new ChildError { Code = NwpErrorCodes.AuthNidScopeViolation, Message = ssrfError },
            };
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCtx.RequestAborted);
        cts.CancelAfter(_options.ChildFetchTimeout);

        var req = new HttpRequestMessage(HttpMethod.Post, gref.NodeUrl.TrimEnd('/') + "/query")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(frame, Json),
                Encoding.UTF8,
                NwpHttpHeaders.MimeFrame),
        };

        // Propagate depth and trace headers to the child so it can enforce cycle rules.
        req.Headers.TryAddWithoutValidation(NwpHttpHeaders.Depth, childDepth.ToString());
        req.Headers.TryAddWithoutValidation(TraceHeader,         string.Join(',', nextTrace));

        // Forward the Agent and Budget headers if present — the child enforces auth itself.
        if (parentCtx.Request.Headers.TryGetValue(NwpHttpHeaders.Agent, out var agent))
            req.Headers.TryAddWithoutValidation(NwpHttpHeaders.Agent, agent.ToString());
        if (parentCtx.Request.Headers.TryGetValue(NwpHttpHeaders.Budget, out var budget))
            req.Headers.TryAddWithoutValidation(NwpHttpHeaders.Budget, budget.ToString());

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                return new ChildFetchResult
                {
                    Rel  = gref.Rel,
                    Node = gref.NodeUrl,
                    Error = new ChildError
                    {
                        Code    = NwpErrorCodes.NodeUnavailable,
                        Message = $"child '{gref.Rel}' returned {(int)resp.StatusCode}: {Truncate(body)}",
                    },
                };
            }

            JsonElement capsule;
            try
            {
                capsule = JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException jx)
            {
                return new ChildFetchResult
                {
                    Rel  = gref.Rel,
                    Node = gref.NodeUrl,
                    Error = new ChildError
                    {
                        Code    = NwpErrorCodes.NodeUnavailable,
                        Message = $"child '{gref.Rel}' returned non-JSON body: {jx.Message}",
                    },
                };
            }

            return new ChildFetchResult
            {
                Rel  = gref.Rel,
                Node = gref.NodeUrl,
                Data = capsule,
            };
        }
        catch (OperationCanceledException)
        {
            return new ChildFetchResult
            {
                Rel   = gref.Rel,
                Node  = gref.NodeUrl,
                Error = new ChildError { Code = NwpErrorCodes.NodeUnavailable, Message = $"child '{gref.Rel}' fetch timed out." },
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Child fetch failed: {Rel} {Node}", gref.Rel, gref.NodeUrl);
            return new ChildFetchResult
            {
                Rel   = gref.Rel,
                Node  = gref.NodeUrl,
                Error = new ChildError { Code = NwpErrorCodes.NodeUnavailable, Message = ex.Message },
            };
        }
    }

    // ── /invoke ──────────────────────────────────────────────────────────────

    private async Task HandleInvoke(HttpContext ctx)
    {
        ActionFrame frame;
        try
        {
            frame = await ReadFrame<ActionFrame>(ctx);
        }
        catch (Exception ex)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid, ex.Message);
            return;
        }

        if (!_options.Actions.TryGetValue(frame.ActionId, out var spec))
        {
            await WriteError(ctx, 404, "NPS-CLIENT-NOT-FOUND",
                NwpErrorCodes.ActionNotFound,
                $"Unknown action_id '{frame.ActionId}'.");
            return;
        }

        // Complex Node does not own the async task machinery — Agents MUST call the
        // appropriate downstream Action Node for long-running jobs.
        if (frame.Async)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid,
                "Complex Node does not support async actions; invoke a downstream Action Node instead.");
            return;
        }

        if (frame.Priority is not null &&
            frame.Priority is not "low" and not "normal" and not "high")
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid,
                $"priority '{frame.Priority}' is invalid (allowed: low/normal/high).");
            return;
        }

        var timeoutMs = ClampTimeout(frame.TimeoutMs, spec);

        var agentNid = ctx.Request.Headers.TryGetValue(NwpHttpHeaders.Agent, out var a)
            ? a.ToString() : null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        ActionExecutionResult result;
        try
        {
            result = await _provider.ExecuteAsync(frame, new ActionContext
            {
                AgentNid  = agentNid,
                RequestId = frame.RequestId,
                TaskId    = null,
                Spec      = spec,
                TimeoutMs = timeoutMs,
                Priority  = frame.Priority ?? "normal",
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            await WriteError(ctx, 504, "NPS-SERVER-TIMEOUT",
                NwpErrorCodes.NodeUnavailable, "action execution timed out.");
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Complex Node action {ActionId} failed", frame.ActionId);
            await WriteError(ctx, 500, "NPS-SERVER-INTERNAL",
                NwpErrorCodes.NodeUnavailable, "action execution failed.");
            return;
        }

        var dataList = result.Result is null
            ? Array.Empty<JsonElement>()
            : new[] { result.Result.Value };

        var caps = new CapsFrame
        {
            AnchorRef = result.AnchorRef ?? spec.ResultAnchor,
            Count     = (uint)dataList.Length,
            Data      = dataList,
            TokenEst  = result.TokenEst,
        };

        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeCapsule;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "complex";
        if (caps.AnchorRef is not null)
            ctx.Response.Headers[NwpHttpHeaders.Schema] = caps.AnchorRef;
        if (result.TokenEst > 0)
            ctx.Response.Headers[NwpHttpHeaders.Tokens] = result.TokenEst.ToString();
        if (frame.RequestId is not null)
            ctx.Response.Headers["X-NWP-Request-Id"] = frame.RequestId;

        await ctx.Response.WriteAsync(JsonSerializer.Serialize(caps, Json));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool TryParseDepth(HttpContext ctx, out uint depth, out string? error)
    {
        depth = 0;
        error = null;

        if (!ctx.Request.Headers.TryGetValue(NwpHttpHeaders.Depth, out var raw))
            return true;

        if (!uint.TryParse(raw.ToString(), out depth))
        {
            error = $"X-NWP-Depth '{raw}' is not a non-negative integer.";
            return false;
        }

        if (depth > _options.GraphMaxDepth)
        {
            error = $"X-NWP-Depth {depth} exceeds node max_depth {_options.GraphMaxDepth}.";
            return false;
        }

        if (depth > ComplexNodeOptions.AbsoluteMaxDepth)
        {
            error = $"X-NWP-Depth {depth} exceeds NPS-2 §11 absolute cap {ComplexNodeOptions.AbsoluteMaxDepth}.";
            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> ParseTrace(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue(TraceHeader, out var raw))
            return Array.Empty<string>();

        return raw.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private uint ClampTimeout(uint requested, ActionSpec spec)
    {
        var specMax = spec.TimeoutMsMax ?? _options.MaxTimeoutMs;
        var hardMax = Math.Min(specMax, _options.MaxTimeoutMs);
        if (requested == 0)
            return spec.TimeoutMsDefault ?? _options.DefaultTimeoutMs;
        return requested > hardMax ? hardMax : requested;
    }

    private static string Truncate(string s) => s.Length <= 256 ? s : s[..256] + "…";

    private static async Task<T> ReadFrame<T>(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ms.Position = 0;
        return JsonSerializer.Deserialize<T>(ms.ToArray(), Json)
            ?? throw new InvalidOperationException("Failed to deserialize frame.");
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
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(err, Json));
    }

    // ── Static payload builder ───────────────────────────────────────────────

    private static (string nwmJson, string schemaJson, string actionsJson, string anchorId)
        BuildStaticPayloads(ComplexNodeOptions opt)
    {
        var baseUrl = opt.PathPrefix.TrimEnd('/');

        // Schema — only computed when the Complex Node exposes a local row schema.
        string schemaJson = "{}";
        string anchorId   = string.Empty;
        Dictionary<string, string>? schemaAnchors = null;

        if (opt.Schema is not null)
        {
            var frameSchema = new FrameSchema
            {
                Fields = opt.Schema.Fields
                    .Select(f => new SchemaField(f.Name, f.Type, Nullable: f.Nullable))
                    .ToList(),
            };
            schemaJson = JsonSerializer.Serialize(frameSchema, Json);
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(schemaJson));
            anchorId      = "sha256:" + Convert.ToHexString(hashBytes).ToLowerInvariant();
            schemaAnchors = new Dictionary<string, string> { ["default"] = anchorId };
        }

        var endpoints = new NodeEndpoints
        {
            Query  = $"{baseUrl}/query",
            Invoke = opt.Actions.Count > 0 ? $"{baseUrl}/invoke" : null,
            Schema = $"{baseUrl}/.schema",
        };

        // Expose graph refs in the NWM per NPS-2 §11.
        var graph = opt.Graph.Count > 0
            ? new NodeGraph
              {
                  Refs     = opt.Graph
                              .Select(r => new NodeGraphRef(r.Rel, r.NodeUrl))
                              .ToList(),
                  MaxDepth = opt.GraphMaxDepth,
              }
            : null;

        var nwm = new NeuralWebManifest
        {
            Nwp             = "0.4",
            NodeId          = opt.NodeId,
            NodeType        = "complex",
            DisplayName     = opt.DisplayName,
            WireFormats     = ["ncp-capsule", "json"],
            PreferredFormat = "json",
            SchemaAnchors   = schemaAnchors,
            Capabilities    = new NodeCapabilities
            {
                Query           = opt.Schema is not null || opt.Graph.Count > 0,
                Stream          = false,
                TokenBudgetHint = true,
            },
            Auth = new NodeAuth
            {
                Required     = opt.RequireAuth,
                IdentityType = opt.RequireAuth ? "nip-cert" : "none",
            },
            Endpoints = endpoints,
            Graph     = graph,
        };
        var nwmJson = JsonSerializer.Serialize(nwm, Json);

        var actionsJson = JsonSerializer.Serialize(
            new { actions = opt.Actions }, Json);

        return (nwmJson, schemaJson, actionsJson, anchorId);
    }
}

// ── Private DTOs for the enhanced CapsFrame ──────────────────────────────────

/// <summary>
/// Complex Node extension of <see cref="CapsFrame"/> carrying an additional
/// <c>graph</c> array. Emitted when the request asked for graph expansion and at
/// least one child was fetched (successfully or not).
/// </summary>
internal sealed record ComplexCapsFrame
{
    [JsonPropertyName("anchor_ref")]
    public string? AnchorRef { get; init; }

    public uint Count { get; init; }

    public IReadOnlyList<JsonElement> Data { get; init; } = Array.Empty<JsonElement>();

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }

    /// <summary>Expanded child nodes. <c>null</c> when no graph expansion happened.</summary>
    public JsonElement? Graph { get; init; }
}

internal sealed record ChildFetchResult
{
    public required string Rel  { get; init; }
    public required string Node { get; init; }

    public JsonElement?  Data  { get; init; }
    public ChildError?   Error { get; init; }
}

internal sealed record ChildError
{
    public required string Code    { get; init; }
    public required string Message { get; init; }
}
