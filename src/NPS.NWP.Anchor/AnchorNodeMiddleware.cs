// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPS.Core.Frames.Ncp;
using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
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
        var budgetNpt        = ReadBudget(ctx);
        var nptCostHint      = spec.EstimatedNpt ?? 0;

        // Rate limit check (per-consumer).
        var consumerKey = agentNid ?? "anonymous";
        var rate = _limiter.TryAcquire(consumerKey, nptCostHint, _options.RateLimits);
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
                BudgetNpt          = budgetNpt,
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
                    frame.RequestId, spec.EstimatedNpt);
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
                if (spec.EstimatedNpt      is { } en)    writer.WriteNumber("estimated_npt",       en);
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
