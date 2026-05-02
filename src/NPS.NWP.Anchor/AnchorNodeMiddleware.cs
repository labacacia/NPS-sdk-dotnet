// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NPS.Core.Frames.Ncp;
using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using NPS.NWP.Anchor.Topology;
using NPS.NWP.Frames;
using NPS.NWP.Http;
using NPS.NWP.Nwm;

namespace NPS.NWP.Anchor;

/// <summary>
/// ASP.NET Core middleware exposing a single Anchor Node
/// (NPS-AaaS §2) at a configurable path prefix. Sub-paths:
/// <c>/.nwm</c>, <c>/.schema</c>, <c>/actions</c>, <c>/invoke</c>.
///
/// <para>
/// On <c>/invoke</c> the middleware validates auth + rate limits, hands the
/// <see cref="ActionFrame"/> to the registered <see cref="IAnchorRouter"/>,
/// then dispatches the resulting <see cref="NPS.NOP.Frames.TaskFrame"/> to
/// the local <see cref="INopOrchestrator"/>. Synchronous actions return a
/// <see cref="CapsFrame"/> with the aggregated result; asynchronous actions
/// return HTTP 202 with a <c>task_id</c> and poll URL (consumers poll
/// <c>system.task.status</c> against the backing Action Node).
/// </para>
///
/// <para>The Anchor itself is stateless — it owns no business logic, no
/// persistent state, and no downstream caches. That lets it scale
/// horizontally behind a load balancer without coordination.</para>
/// </summary>
public sealed class AnchorNodeMiddleware
{
    private readonly RequestDelegate       _next;
    private readonly AnchorNodeOptions    _options;
    private readonly IAnchorRouter        _router;
    private readonly INopOrchestrator      _orchestrator;
    private readonly IAnchorRateLimiter   _limiter;
    private readonly ILogger               _log;

    private readonly string _nwmJson;
    private readonly string _actionsJson;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = false,
    };

    public AnchorNodeMiddleware(
        RequestDelegate                 next,
        AnchorNodeOptions              options,
        IAnchorRouter                  router,
        INopOrchestrator                orchestrator,
        IAnchorRateLimiter             limiter,
        ILogger<AnchorNodeMiddleware>  logger)
    {
        _next         = next;
        _options      = options;
        _router       = router;
        _orchestrator = orchestrator;
        _limiter      = limiter;
        _log          = logger;

        (_nwmJson, _actionsJson) = BuildStaticPayloads(_options);
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

        // Auth gate (before rate limiter so unauthenticated attacks don't
        // consume per-consumer buckets).
        if (_options.RequireAuth && !ctx.Request.Headers.ContainsKey(NwpHttpHeaders.Agent))
        {
            await WriteError(ctx, 401, "NPS-AUTH-UNAUTHENTICATED",
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
            case "/actions":
            case "/actions/":
                await HandleActions(ctx);
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

            case "/query":
            case "/query/":
                if (ctx.Request.Method != HttpMethods.Post)
                {
                    ctx.Response.StatusCode = 405;
                    return;
                }
                await HandleQuery(ctx);
                break;

            case "/subscribe":
            case "/subscribe/":
                if (ctx.Request.Method != HttpMethods.Post)
                {
                    ctx.Response.StatusCode = 405;
                    return;
                }
                await HandleSubscribe(ctx);
                break;

            default:
                await _next(ctx);
                break;
        }
    }

    // ── /query (reserved query types — NPS-2 §12) ────────────────────────────

    private async Task HandleQuery(HttpContext ctx)
    {
        if (!CheckTopologyCapability(ctx))
            return;

        JsonElement body;
        try
        {
            body = await ReadJson(ctx);
        }
        catch (Exception ex)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.QueryFilterInvalid, ex.Message);
            return;
        }

        var type = body.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type != TopologyWire.TypeSnapshot)
        {
            // NPS-2 §12: unknown reserved-type identifiers MUST return
            // NWP-RESERVED-TYPE-UNSUPPORTED (NPS-SERVER-UNSUPPORTED / HTTP 501),
            // not NWP-ACTION-NOT-FOUND — TC-N2-AnchorTopo-08.
            await WriteError(ctx, 501, "NPS-SERVER-UNSUPPORTED",
                NwpTopologyErrorCodes.ReservedTypeUnsupported,
                type is null
                    ? "Anchor /query requires a reserved type per NPS-2 §12."
                    : $"Reserved query type '{type}' is not implemented by this Anchor Node.");
            return;
        }

        var topology = ctx.RequestServices.GetService<IAnchorTopologyService>();
        if (topology is null)
        {
            await WriteError(ctx, 501, "NPS-SERVER-UNSUPPORTED",
                NwpErrorCodes.NodeUnavailable,
                "topology.snapshot is not available — IAnchorTopologyService is not registered.");
            return;
        }

        TopologySnapshotRequest req;
        try
        {
            req = ParseSnapshotRequest(body);
        }
        catch (TopologyProtocolException ex)
        {
            await WriteError(ctx, ex.NpsStatus == "NPS-AUTH-FORBIDDEN" ? 403 : 400,
                ex.NpsStatus, ex.NwpErrorCode, ex.Message);
            return;
        }

        try
        {
            var snapshot = await topology.GetSnapshotAsync(req, ctx.RequestAborted);
            var capsData = JsonSerializer.SerializeToElement(snapshot, Json);

            var caps = new CapsFrame
            {
                AnchorRef = TopologyWire.SnapshotAnchorRef,
                Count     = 1,
                Data      = new[] { capsData },
            };

            ctx.Response.StatusCode  = 200;
            ctx.Response.ContentType = NwpHttpHeaders.MimeCapsule;
            ctx.Response.Headers[NwpHttpHeaders.NodeType] = "anchor";
            ctx.Response.Headers[NwpHttpHeaders.Schema]   = TopologyWire.SnapshotAnchorRef;
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(caps, Json));
        }
        catch (TopologyProtocolException ex)
        {
            await WriteError(ctx, ex.NpsStatus == "NPS-AUTH-FORBIDDEN" ? 403 : 400,
                ex.NpsStatus, ex.NwpErrorCode, ex.Message);
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "topology.snapshot failed");
            await WriteError(ctx, 500, "NPS-SERVER-INTERNAL",
                NwpErrorCodes.NodeUnavailable, "topology snapshot failed.");
        }
    }

    // ── /subscribe (reserved subscribe types — NPS-2 §12) ────────────────────

    private async Task HandleSubscribe(HttpContext ctx)
    {
        if (!CheckTopologyCapability(ctx))
            return;

        JsonElement body;
        try
        {
            body = await ReadJson(ctx);
        }
        catch (Exception ex)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.QueryFilterInvalid, ex.Message);
            return;
        }

        var type = body.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type != TopologyWire.TypeStream)
        {
            await WriteError(ctx, 501, "NPS-SERVER-UNSUPPORTED",
                NwpTopologyErrorCodes.ReservedTypeUnsupported,
                type is null
                    ? "Anchor /subscribe requires a reserved type per NPS-2 §12."
                    : $"Reserved subscribe type '{type}' is not implemented by this Anchor Node.");
            return;
        }

        var topology = ctx.RequestServices.GetService<IAnchorTopologyService>();
        if (topology is null)
        {
            await WriteError(ctx, 501, "NPS-SERVER-UNSUPPORTED",
                NwpErrorCodes.NodeUnavailable,
                "topology.stream is not available — IAnchorTopologyService is not registered.");
            return;
        }

        TopologyStreamRequest req;
        string streamId;
        try
        {
            (req, streamId) = ParseStreamRequest(body);
        }
        catch (TopologyProtocolException ex)
        {
            await WriteError(ctx, ex.NpsStatus == "NPS-AUTH-FORBIDDEN" ? 403 : 400,
                ex.NpsStatus, ex.NwpErrorCode, ex.Message);
            return;
        }

        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeCapsule;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "anchor";

        // First line: subscription ack (mirrors §8.3 ack CapsFrame in NDJSON form).
        var ack = new TopologySubscribeAck
        {
            StreamId = streamId,
            Status   = "subscribed",
            LastSeq  = 0,
            Resumed  = req.SinceVersion is not null,
        };
        await WriteJsonLine(ctx, ack);

        try
        {
            await foreach (var ev in topology.SubscribeAsync(req, ctx.RequestAborted))
            {
                if (ctx.RequestAborted.IsCancellationRequested) break;
                var envelope = ToEnvelope(streamId, ev);
                await WriteJsonLine(ctx, envelope);
                if (ev is ResyncRequired) break;   // §12.2: subscriber MUST re-snapshot
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (TopologyProtocolException ex)
        {
            // Mid-stream protocol error: emit a final-line error envelope so
            // the client can surface it without parsing an HTTP-level error.
            var err = new ErrorFrame
            {
                Status  = ex.NpsStatus,
                Error   = ex.NwpErrorCode,
                Message = ex.Message,
            };
            await WriteJsonLine(ctx, err);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "topology.stream failed mid-stream");
        }
    }

    // ── Reserved-type request parsing ───────────────────────────────────────

    private static TopologySnapshotRequest ParseSnapshotRequest(JsonElement body)
    {
        if (!body.TryGetProperty("topology", out var topo) || topo.ValueKind != JsonValueKind.Object)
            throw new TopologyProtocolException(NwpTopologyErrorCodes.UnsupportedScope,
                "NPS-CLIENT-BAD-PARAM",
                "topology.snapshot requires a 'topology' object per NPS-2 §12.1.");

        var scope = ParseScope(topo);
        var include = ParseInclude(topo);
        var depth = ParseDepth(topo);
        string? targetNid = null;
        if (topo.TryGetProperty("target_nid", out var tn) && tn.ValueKind == JsonValueKind.String)
            targetNid = tn.GetString();

        if (scope == TopologyScope.Member && string.IsNullOrEmpty(targetNid))
            throw new TopologyProtocolException(NwpTopologyErrorCodes.UnsupportedScope,
                "NPS-CLIENT-BAD-PARAM",
                "topology.target_nid is required when topology.scope = \"member\".");

        return new TopologySnapshotRequest
        {
            Scope     = scope,
            Include   = include,
            Depth     = depth,
            TargetNid = targetNid,
        };
    }

    private static (TopologyStreamRequest req, string streamId) ParseStreamRequest(JsonElement body)
    {
        if (!body.TryGetProperty("topology", out var topo) || topo.ValueKind != JsonValueKind.Object)
            throw new TopologyProtocolException(NwpTopologyErrorCodes.UnsupportedScope,
                "NPS-CLIENT-BAD-PARAM",
                "topology.stream requires a 'topology' object per NPS-2 §12.2.");

        var scope = ParseScope(topo);

        TopologyFilter? filter = null;
        if (topo.TryGetProperty("filter", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            filter = JsonSerializer.Deserialize<TopologyFilter>(f.GetRawText(), Json);
            ValidateFilterKeys(f);
        }

        ulong? since = null;
        if (topo.TryGetProperty("since_version", out var sv) && sv.TryGetUInt64(out var svVal))
            since = svVal;
        else if (body.TryGetProperty("resume_from_seq", out var rfs) && rfs.TryGetUInt64(out var rfsVal))
            // §12.2: topology.since_version is the topology synonym of
            // SubscribeFrame.resume_from_seq. Accept either; the topology
            // form takes precedence when both are present (already handled
            // above by the if/else order).
            since = rfsVal;

        var streamId = body.TryGetProperty("stream_id", out var sid) && sid.ValueKind == JsonValueKind.String
            ? sid.GetString() ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");

        return (new TopologyStreamRequest
        {
            Scope        = scope,
            Filter       = filter,
            SinceVersion = since,
        }, streamId);
    }

    private static TopologyScope ParseScope(JsonElement topo)
    {
        if (!topo.TryGetProperty("scope", out var s) || s.ValueKind != JsonValueKind.String)
            return TopologyScope.Cluster;
        var v = s.GetString();
        return v switch
        {
            TopologyWire.ScopeCluster => TopologyScope.Cluster,
            TopologyWire.ScopeMember  => TopologyScope.Member,
            _ => throw new TopologyProtocolException(NwpTopologyErrorCodes.UnsupportedScope,
                "NPS-CLIENT-BAD-PARAM", $"unknown topology.scope '{v}'."),
        };
    }

    private static TopologyInclude ParseInclude(JsonElement topo)
    {
        if (!topo.TryGetProperty("include", out var i) || i.ValueKind != JsonValueKind.Array)
            return TopologyInclude.Default;
        var flags = TopologyInclude.None;
        foreach (var item in i.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            flags |= item.GetString() switch
            {
                TopologyWire.IncludeMembers      => TopologyInclude.Members,
                TopologyWire.IncludeCapabilities => TopologyInclude.Capabilities,
                TopologyWire.IncludeTags         => TopologyInclude.Tags,
                TopologyWire.IncludeMetrics      => TopologyInclude.Metrics,
                _ => TopologyInclude.None,
            };
        }
        return flags == TopologyInclude.None ? TopologyInclude.Default : flags;
    }

    private static byte ParseDepth(JsonElement topo)
    {
        if (topo.TryGetProperty("depth", out var d) && d.TryGetByte(out var v))
            return v == 0 ? (byte)1 : v;
        return 1;
    }

    private static void ValidateFilterKeys(JsonElement filter)
    {
        // Documented keys per §12.2 — anything else is a wire-format error.
        foreach (var prop in filter.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "tags_any":
                case "tags_all":
                case "node_roles":
                case "node_kind":   // backward-compat alias for node_roles (NPS-CR-0001 / M1; accepted through alpha.5)
                    continue;
                default:
                    throw new TopologyProtocolException(NwpTopologyErrorCodes.FilterUnsupported,
                        "NPS-CLIENT-BAD-PARAM",
                        $"topology.filter key '{prop.Name}' is not recognized.");
            }
        }
    }

    private static TopologyEventEnvelope ToEnvelope(string streamId, TopologyEvent ev)
    {
        var ts = DateTimeOffset.UtcNow.ToString("O");
        return ev switch
        {
            MemberJoined j  => Make(streamId, j.Version, TopologyWire.EventMemberJoined, ts,
                                    JsonSerializer.SerializeToElement(j.Member, Json)),
            MemberLeft l    => Make(streamId, l.Version, TopologyWire.EventMemberLeft, ts,
                                    JsonSerializer.SerializeToElement(new { nid = l.Nid }, Json)),
            MemberUpdated u => Make(streamId, u.Version, TopologyWire.EventMemberUpdated, ts,
                                    JsonSerializer.SerializeToElement(new { nid = u.Nid, changes = u.Changes }, Json)),
            AnchorState s   => Make(streamId, s.Version, TopologyWire.EventAnchorState, ts,
                                    JsonSerializer.SerializeToElement(new { field = s.Field, details = s.Details }, Json)),
            ResyncRequired r => Make(streamId, null, TopologyWire.EventResyncRequired, ts,
                                    JsonSerializer.SerializeToElement(new { reason = r.Reason }, Json)),
            _ => throw new InvalidOperationException($"unknown TopologyEvent subtype: {ev.GetType().Name}"),
        };
    }

    private static TopologyEventEnvelope Make(
        string streamId, ulong? seq, string eventType, string ts, JsonElement payload)
    {
        // token-budget §7.2: SHOULD report CGN per event; use UTF-8/4 fallback.
        var npt = (uint)Math.Max(1, System.Text.Encoding.UTF8.GetByteCount(payload.GetRawText()) / 4);
        return new TopologyEventEnvelope
        {
            StreamId  = streamId,
            Seq       = seq,
            EventType = eventType,
            Timestamp = ts,
            Payload   = payload,
            CgnEst    = npt,
        };
    }

    private static async Task WriteJsonLine<T>(HttpContext ctx, T value)
    {
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(value, Json) + "\n");
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    private static async Task<JsonElement> ReadJson(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ms.Position = 0;
        return JsonSerializer.Deserialize<JsonElement>(ms.ToArray(), Json);
    }

    // ── Static endpoints ─────────────────────────────────────────────────────

    private Task HandleNwm(HttpContext ctx)
    {
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeManifest;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "anchor";
        return ctx.Response.WriteAsync(_nwmJson);
    }

    private Task HandleActions(HttpContext ctx)
    {
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(_actionsJson);
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

        if (frame.Priority is not null &&
            frame.Priority is not "low" and not "normal" and not "high")
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid,
                $"priority '{frame.Priority}' is invalid (allowed: low/normal/high).");
            return;
        }

        if (frame.Async && !spec.Async)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid,
                $"action '{frame.ActionId}' does not support async execution.");
            return;
        }

        var agentNid = ctx.Request.Headers.TryGetValue(NwpHttpHeaders.Agent, out var a)
            ? a.ToString() : null;
        var priority         = frame.Priority ?? "normal";
        var effectiveTimeout = ClampTimeout(frame.TimeoutMs, spec);
        var budgetCgn        = ReadBudget(ctx);
        var cgnCostHint      = spec.EstimatedCgn ?? 0;

        // Rate limit check (per-consumer).
        var consumerKey = agentNid ?? "anonymous";
        var rate = _limiter.TryAcquire(consumerKey, cgnCostHint, _options.RateLimits);
        if (!rate.Allowed)
        {
            if (rate.RetryAfterSeconds is { } retry)
                ctx.Response.Headers["Retry-After"] = retry.ToString();
            await WriteError(ctx, 429, "NPS-LIMIT-RATE",
                NwpErrorCodes.BudgetExceeded,
                rate.Reason ?? "rate limit exceeded.");
            return;
        }

        try
        {
            var routeCtx = new AnchorRouteContext
            {
                Spec               = spec,
                AgentNid           = agentNid,
                RequestId          = frame.RequestId,
                EffectiveTimeoutMs = effectiveTimeout,
                Priority           = priority,
                TraceContext       = BuildTraceContext(ctx),
                BudgetCgn          = budgetCgn,
            };

            TaskFrame task;
            try
            {
                task = await _router.BuildTaskAsync(frame, routeCtx, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Anchor router failed to build TaskFrame for {ActionId}",
                    frame.ActionId);
                await WriteError(ctx, 500, "NPS-SERVER-INTERNAL",
                    NwpErrorCodes.NodeUnavailable,
                    "anchor failed to route the action.");
                return;
            }

            if (frame.Async)
            {
                // Fire-and-forget. The returned task_id is the NOP TaskFrame.TaskId so
                // the consumer can poll via system.task.status on the backing Action Node.
                _ = Task.Run(async () =>
                {
                    try     { await _orchestrator.ExecuteAsync(task); }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Async orchestration failed for task {TaskId}",
                            task.TaskId);
                    }
                });

                await WriteAsyncResponse(ctx, task.TaskId, "pending", frame.RequestId,
                    estimatedMs: spec.TimeoutMsDefault);
                return;
            }

            // Synchronous path — wait for the orchestrator to reach a terminal state.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            cts.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeout));

            NopTaskResult result;
            try
            {
                result = await _orchestrator.ExecuteAsync(task, cts.Token);
            }
            catch (OperationCanceledException)
            {
                await WriteError(ctx, 504, "NPS-SERVER-TIMEOUT",
                    NwpErrorCodes.NodeUnavailable,
                    "anchor task timed out.");
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Orchestration failed for action {ActionId}", frame.ActionId);
                await WriteError(ctx, 500, "NPS-SERVER-INTERNAL",
                    NwpErrorCodes.NodeUnavailable,
                    "anchor task execution failed.");
                return;
            }

            if (result.FinalState == TaskState.Completed)
            {
                await WriteCaps(ctx, result.AggregatedResult, spec.ResultAnchor,
                    frame.RequestId, spec.EstimatedCgn);
            }
            else
            {
                var status = result.FinalState == TaskState.Cancelled
                    ? "NPS-CLIENT-GONE"
                    : "NPS-SERVER-INTERNAL";
                await WriteError(ctx,
                    result.FinalState == TaskState.Cancelled ? 410 : 500,
                    status,
                    result.ErrorCode   ?? NwpErrorCodes.NodeUnavailable,
                    result.ErrorMessage ?? "orchestration did not complete.");
            }
        }
        finally
        {
            _limiter.Release(consumerKey);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if the request is allowed to access topology endpoints.
    /// When <see cref="AnchorNodeOptions.RequireTopologyCapability"/> is set, the
    /// caller MUST declare <c>"topology:read"</c> in <c>X-NWP-Capabilities</c>;
    /// otherwise writes a 403 and returns <c>false</c>.
    /// </summary>
    private bool CheckTopologyCapability(HttpContext ctx)
    {
        if (!_options.RequireTopologyCapability)
            return true;

        var raw = ctx.Request.Headers.TryGetValue(NwpHttpHeaders.Capabilities, out var v)
            ? v.ToString() : string.Empty;

        if (raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
               .Contains("topology:read", StringComparer.OrdinalIgnoreCase))
            return true;

        _ = WriteError(ctx, 403, "NPS-AUTH-FORBIDDEN",
            NwpTopologyErrorCodes.Unauthorized,
            "Caller must declare 'topology:read' in X-NWP-Capabilities to access topology endpoints.");
        return false;
    }

    private uint ClampTimeout(uint requested, AnchorActionSpec spec)
    {
        var specMax = spec.TimeoutMsMax ?? _options.MaxTimeoutMs;
        var hardMax = Math.Min(specMax, _options.MaxTimeoutMs);

        if (requested == 0)
            return spec.TimeoutMsDefault ?? _options.DefaultTimeoutMs;
        return requested > hardMax ? hardMax : requested;
    }

    private uint ReadBudget(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue(NwpHttpHeaders.Budget, out var v) &&
            uint.TryParse(v.ToString(), out var parsed))
            return parsed;
        return _options.DefaultTokenBudget;
    }

    /// <summary>
    /// Build or forward a W3C <see cref="TaskContext"/>. Consumer can supply
    /// <c>traceparent</c> / <c>tracestate</c>; otherwise — when
    /// <see cref="AnchorNodeOptions.AutoInjectTraceContext"/> is set — the
    /// anchor mints a fresh trace id so downstream NOP spans link up.
    /// </summary>
    private TaskContext? BuildTraceContext(HttpContext ctx)
    {
        var (traceId, spanId, flags) = ReadTraceparent(ctx.Request.Headers["traceparent"].ToString());

        if (traceId is null && !_options.AutoInjectTraceContext)
            return null;

        traceId ??= RandomHex(16);   // 32 chars
        spanId  ??= RandomHex(8);    //  ↑ always regenerate the child span id

        return new TaskContext
        {
            TraceId    = traceId,
            SpanId     = spanId,
            TraceFlags = flags ?? 0x01,  // sampled
        };
    }

    private static (string? traceId, string? spanId, byte? flags) ReadTraceparent(string header)
    {
        // traceparent: "00-<trace_id 32>-<parent_id 16>-<flags 2>"
        if (string.IsNullOrWhiteSpace(header)) return (null, null, null);
        var parts = header.Split('-');
        if (parts.Length != 4 || parts[0] != "00") return (null, null, null);
        if (parts[1].Length != 32 || parts[2].Length != 16 || parts[3].Length != 2)
            return (null, null, null);
        byte.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, null, out var flags);
        // Per W3C, the anchor becomes a new parent — mint a fresh span_id so downstream
        // children link back to it rather than the caller's span.
        return (parts[1], RandomHex(8), flags);
    }

    private static string RandomHex(int bytes)
    {
        Span<byte> buf = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    private static async Task<T> ReadFrame<T>(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ms.Position = 0;
        return JsonSerializer.Deserialize<T>(ms.ToArray(), Json)
            ?? throw new InvalidOperationException("Failed to deserialize frame.");
    }

    private Task WriteAsyncResponse(
        HttpContext ctx, string taskId, string status,
        string? requestId, uint? estimatedMs)
    {
        var body = new AsyncActionResponse
        {
            TaskId      = taskId,
            Status      = status,
            PollUrl     = $"{_options.PathPrefix.TrimEnd('/')}/invoke",
            EstimatedMs = estimatedMs,
            RequestId   = requestId,
        };
        ctx.Response.StatusCode  = 202;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "anchor";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }

    private Task WriteCaps(
        HttpContext ctx, JsonElement? payload, string? anchorRef,
        string? requestId, uint? tokenEst)
    {
        var dataList = payload is null
            ? Array.Empty<JsonElement>()
            : new[] { payload.Value };

        var caps = new CapsFrame
        {
            AnchorRef = anchorRef ?? string.Empty,
            Count     = (uint)dataList.Length,
            Data      = dataList,
            TokenEst  = tokenEst,
        };

        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeCapsule;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "anchor";
        if (!string.IsNullOrEmpty(anchorRef))
            ctx.Response.Headers[NwpHttpHeaders.Schema] = anchorRef;
        if (tokenEst is { } est && est > 0)
            ctx.Response.Headers[NwpHttpHeaders.Tokens] = est.ToString();
        if (requestId is not null)
            ctx.Response.Headers["X-NWP-Request-Id"] = requestId;

        return ctx.Response.WriteAsync(JsonSerializer.Serialize(caps, Json));
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

    // ── NWM payload ──────────────────────────────────────────────────────────

    private static (string nwmJson, string actionsJson) BuildStaticPayloads(AnchorNodeOptions opt)
    {
        var baseUrl = opt.PathPrefix.TrimEnd('/');

        var nwm = new NeuralWebManifest
        {
            Nwp             = "0.4",
            NodeId          = opt.NodeId,
            NodeType        = "anchor",
            DisplayName     = opt.DisplayName,
            WireFormats     = ["ncp-capsule", "json"],
            PreferredFormat = "json",
            Capabilities    = new NodeCapabilities
            {
                Query           = false,
                Stream          = false,
                TokenBudgetHint = true,
            },
            Auth = new NodeAuth
            {
                Required             = opt.RequireAuth,
                IdentityType         = opt.RequireAuth ? "nip-cert" : "none",
                RequiredCapabilities = opt.RequiredCapabilities,
            },
            Endpoints = new NodeEndpoints
            {
                Invoke = $"{baseUrl}/invoke",
                Schema = $"{baseUrl}/.schema",
            },
        };

        // Serialize the NWM as a base, then splice in the rate_limits block which
        // is part of NPS-AaaS but not yet in the canonical NWM record.
        var nwmRoot = JsonSerializer.SerializeToElement(nwm, Json);
        using var doc = JsonDocument.Parse(nwmRoot.GetRawText());
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
                prop.WriteTo(writer);

            if (opt.RateLimits is not null)
            {
                writer.WritePropertyName("rate_limits");
                JsonSerializer.Serialize(writer, opt.RateLimits, Json);
            }

            // actions block: AaaS manifests include actions directly so that a
            // single fetch of /.nwm tells a consumer everything they need.
            writer.WritePropertyName("actions");
            writer.WriteStartArray();
            foreach (var (id, spec) in opt.Actions)
            {
                writer.WriteStartObject();
                writer.WriteString("action_id",   id);
                if (spec.Description       is not null) writer.WriteString("description",         spec.Description);
                if (spec.ParamsAnchor      is not null) writer.WriteString("params_anchor",       spec.ParamsAnchor);
                if (spec.ResultAnchor      is not null) writer.WriteString("result_anchor",       spec.ResultAnchor);
                if (spec.EstimatedCgn      is { } en)    writer.WriteNumber("cgn_est",             en);
                if (spec.TimeoutMsDefault  is { } td)    writer.WriteNumber("timeout_ms_default",  td);
                if (spec.TimeoutMsMax      is { } tm)    writer.WriteNumber("timeout_ms_max",      tm);
                if (spec.RequiredCapability is not null) writer.WriteString("required_capability", spec.RequiredCapability);
                writer.WriteBoolean("async", spec.Async);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        var nwmJson = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);

        var actionsJson = JsonSerializer.Serialize(
            new { actions = opt.Actions }, Json);

        return (nwmJson, actionsJson);
    }
}
