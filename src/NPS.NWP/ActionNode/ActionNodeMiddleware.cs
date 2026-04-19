// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;
using NPS.NWP.Http;
using NPS.NWP.Nwm;

namespace NPS.NWP.ActionNode;

/// <summary>
/// ASP.NET Core middleware exposing a single Action Node at a configurable path prefix
/// (NPS-2 §3.2, §7). Sub-paths: <c>/.nwm</c>, <c>/.schema</c>, <c>/actions</c>, <c>/invoke</c>.
/// Built-in reserved actions <c>system.task.status</c> and <c>system.task.cancel</c> are
/// handled by the middleware and MUST NOT appear in <see cref="ActionNodeOptions.Actions"/>.
/// </summary>
public sealed class ActionNodeMiddleware
{
    /// <summary>Reserved action id for polling async task state (NPS-2 §7.3).</summary>
    public const string SystemTaskStatus = "system.task.status";
    /// <summary>Reserved action id for cancelling a running async task (NPS-2 §7.3).</summary>
    public const string SystemTaskCancel = "system.task.cancel";

    private readonly RequestDelegate       _next;
    private readonly IActionNodeProvider   _provider;
    private readonly ActionNodeOptions     _options;
    private readonly IActionTaskStore      _taskStore;
    private readonly IIdempotencyCache     _idempotency;
    private readonly ILogger               _logger;

    private readonly string _nwmJson;
    private readonly string _actionsJson;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
    };

    /// <summary>Clock override, primarily for tests. Defaults to <see cref="DateTime.UtcNow"/>.</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    public ActionNodeMiddleware(
        RequestDelegate     next,
        IActionNodeProvider provider,
        ActionNodeOptions   options,
        IActionTaskStore    taskStore,
        IIdempotencyCache   idempotency,
        ILogger<ActionNodeMiddleware> logger)
    {
        _next        = next;
        _provider    = provider;
        _options     = options;
        _taskStore   = taskStore;
        _idempotency = idempotency;
        _logger      = logger;

        if (_options.Actions.ContainsKey(SystemTaskStatus) ||
            _options.Actions.ContainsKey(SystemTaskCancel))
        {
            throw new InvalidOperationException(
                $"Reserved action ids '{SystemTaskStatus}' / '{SystemTaskCancel}' are provided by the middleware and MUST NOT be registered in ActionNodeOptions.Actions.");
        }

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
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "action";
        return ctx.Response.WriteAsync(_nwmJson);
    }

    private Task HandleSchema(HttpContext ctx)
    {
        // Action Node does not own a row schema, but we still answer the conventional
        // /.schema route with the JSON action registry so tools can introspect.
        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(_actionsJson);
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

        // Reserved actions are handled by the middleware itself.
        if (frame.ActionId == SystemTaskStatus)
        {
            await HandleSystemTaskStatus(ctx, frame);
            return;
        }
        if (frame.ActionId == SystemTaskCancel)
        {
            await HandleSystemTaskCancel(ctx, frame);
            return;
        }

        // Look up action spec
        if (!_options.Actions.TryGetValue(frame.ActionId, out var spec))
        {
            await WriteError(ctx, 404, "NPS-CLIENT-NOT-FOUND",
                NwpErrorCodes.ActionNotFound,
                $"Unknown action_id '{frame.ActionId}'.");
            return;
        }

        // Priority validation
        if (frame.Priority is not null &&
            frame.Priority is not "low" and not "normal" and not "high")
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid,
                $"priority '{frame.Priority}' is invalid (allowed: low/normal/high).");
            return;
        }

        // Async-capable check
        if (frame.Async && !spec.Async)
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid,
                $"action '{frame.ActionId}' does not support async execution.");
            return;
        }

        // Timeout clamping
        var effectiveTimeout = ClampTimeout(frame.TimeoutMs, spec);

        // Callback URL SSRF guard (only if provided)
        if (frame.CallbackUrl is not null)
        {
            var err = ActionCallbackValidator.Validate(frame.CallbackUrl, _options.RejectPrivateCallbackUrls);
            if (err is not null)
            {
                await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                    NwpErrorCodes.ActionParamsInvalid, err);
                return;
            }
        }

        // Idempotency check (sync + async). §7.1
        var paramsHash = HashParams(frame.Params);
        if (frame.IdempotencyKey is not null)
        {
            var cached = _idempotency.Get(frame.ActionId, frame.IdempotencyKey);
            if (cached is not null)
            {
                if (cached.ParamsHash != paramsHash)
                {
                    await WriteError(ctx, 409, "NPS-CLIENT-CONFLICT",
                        NwpErrorCodes.ActionIdempotencyConflict,
                        "idempotency_key reuse with different params.");
                    return;
                }

                if (cached.TaskId is not null)
                {
                    // async re-hit — return the original task handle
                    await WriteAsyncResponse(ctx, cached.TaskId, _taskStore.Get(cached.TaskId)?.Status ?? "pending",
                        frame.RequestId, estimatedMs: null);
                    return;
                }

                await WriteCaps(ctx, cached.Result, cached.AnchorRef, frame.RequestId);
                return;
            }
        }

        // Agent / request context
        var agentNid  = ctx.Request.Headers.TryGetValue(NwpHttpHeaders.Agent, out var a)
            ? a.ToString() : null;
        var priority  = frame.Priority ?? "normal";

        if (frame.Async)
        {
            var taskId = Guid.NewGuid().ToString("N");
            _taskStore.Create(taskId, frame.ActionId, frame.RequestId, agentNid);

            // Cache the task id so repeated idempotent async calls return the same handle.
            if (frame.IdempotencyKey is not null)
            {
                _idempotency.TryStore(frame.ActionId, frame.IdempotencyKey, new IdempotentEntry
                {
                    ActionId    = frame.ActionId,
                    ParamsHash  = paramsHash,
                    TaskId      = taskId,
                    ExpiresAt   = Clock().Add(_options.IdempotencyTtl),
                });
            }

            var runCtx = new ActionContext
            {
                AgentNid  = agentNid,
                RequestId = frame.RequestId,
                TaskId    = taskId,
                Spec      = spec,
                TimeoutMs = effectiveTimeout,
                Priority  = priority,
            };

            // Fire-and-forget; lifetime tied to the task (not the request).
            _ = Task.Run(() => RunAsyncTask(frame, runCtx, effectiveTimeout));

            await WriteAsyncResponse(ctx, taskId, "pending", frame.RequestId,
                estimatedMs: spec.TimeoutMsDefault);
            return;
        }

        // Synchronous path
        var syncCtx = new ActionContext
        {
            AgentNid  = agentNid,
            RequestId = frame.RequestId,
            TaskId    = null,
            Spec      = spec,
            TimeoutMs = effectiveTimeout,
            Priority  = priority,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeout));

        ActionExecutionResult result;
        try
        {
            result = await _provider.ExecuteAsync(frame, syncCtx, cts.Token);
        }
        catch (OperationCanceledException)
        {
            await WriteError(ctx, 504, "NPS-SERVER-TIMEOUT", "NWP-NODE-UNAVAILABLE",
                "action execution timed out.");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Action {ActionId} failed", frame.ActionId);
            await WriteError(ctx, 500, "NPS-SERVER-INTERNAL", "NWP-NODE-UNAVAILABLE",
                "action execution failed.");
            return;
        }

        if (frame.IdempotencyKey is not null)
        {
            _idempotency.TryStore(frame.ActionId, frame.IdempotencyKey, new IdempotentEntry
            {
                ActionId   = frame.ActionId,
                ParamsHash = paramsHash,
                Result     = result.Result,
                AnchorRef  = result.AnchorRef ?? spec.ResultAnchor,
                ExpiresAt  = Clock().Add(_options.IdempotencyTtl),
            });
        }

        await WriteCaps(ctx, result.Result, result.AnchorRef ?? spec.ResultAnchor,
            frame.RequestId, result.TokenEst);
    }

    private async Task RunAsyncTask(ActionFrame frame, ActionContext runCtx, uint timeoutMs)
    {
        // Mark as running (only if still pending)
        _taskStore.TryTransition(runCtx.TaskId!, "pending", "running");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            var res = await _provider.ExecuteAsync(frame, runCtx, cts.Token);
            _taskStore.Complete(runCtx.TaskId!, res.Result);
        }
        catch (OperationCanceledException)
        {
            var err = JsonSerializer.SerializeToElement(
                new { code = "NWP-NODE-UNAVAILABLE", message = "task timed out" }, Json);
            _taskStore.Fail(runCtx.TaskId!, err);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Async action {ActionId} (task {TaskId}) failed",
                frame.ActionId, runCtx.TaskId);
            var err = JsonSerializer.SerializeToElement(
                new { code = "NWP-NODE-UNAVAILABLE", message = ex.Message }, Json);
            _taskStore.Fail(runCtx.TaskId!, err);
        }
    }

    // ── system.task.status / system.task.cancel ──────────────────────────────

    private async Task HandleSystemTaskStatus(HttpContext ctx, ActionFrame frame)
    {
        var taskId = ReadStringParam(frame.Params, "task_id");
        if (string.IsNullOrEmpty(taskId))
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid, "params.task_id is required.");
            return;
        }

        var rec = _taskStore.Get(taskId);
        if (rec is null)
        {
            await WriteError(ctx, 404, "NPS-CLIENT-NOT-FOUND",
                NwpErrorCodes.TaskNotFound, $"Unknown task_id '{taskId}'.");
            return;
        }

        var status = new ActionTaskStatus
        {
            TaskId    = rec.TaskId,
            Status    = rec.Status,
            Progress  = rec.Progress,
            CreatedAt = rec.CreatedAt.ToString("O"),
            UpdatedAt = rec.UpdatedAt.ToString("O"),
            RequestId = rec.RequestId,
            Result    = rec.Result,
            Error     = rec.Error,
        };

        var payload = JsonSerializer.SerializeToElement(status, Json);
        await WriteCaps(ctx, payload, anchorRef: null, frame.RequestId);
    }

    private async Task HandleSystemTaskCancel(HttpContext ctx, ActionFrame frame)
    {
        var taskId = ReadStringParam(frame.Params, "task_id");
        if (string.IsNullOrEmpty(taskId))
        {
            await WriteError(ctx, 400, "NPS-CLIENT-BAD-REQUEST",
                NwpErrorCodes.ActionParamsInvalid, "params.task_id is required.");
            return;
        }

        var rec = _taskStore.Get(taskId);
        if (rec is null)
        {
            await WriteError(ctx, 404, "NPS-CLIENT-NOT-FOUND",
                NwpErrorCodes.TaskNotFound, $"Unknown task_id '{taskId}'.");
            return;
        }

        if (rec.Status is "completed" or "failed" or "cancelled")
        {
            await WriteError(ctx, 409, "NPS-CLIENT-CONFLICT",
                NwpErrorCodes.TaskAlreadyCancelled,
                $"Task '{taskId}' is already in a terminal state ('{rec.Status}').");
            return;
        }

        _taskStore.Cancel(taskId);
        var payload = JsonSerializer.SerializeToElement(
            new { task_id = taskId, status = "cancelled" }, Json);
        await WriteCaps(ctx, payload, anchorRef: null, frame.RequestId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private uint ClampTimeout(uint requested, ActionSpec spec)
    {
        // Effective max = min(spec.TimeoutMsMax, options.MaxTimeoutMs)
        var specMax = spec.TimeoutMsMax ?? _options.MaxTimeoutMs;
        var hardMax = Math.Min(specMax, _options.MaxTimeoutMs);

        if (requested == 0)
        {
            return spec.TimeoutMsDefault ?? _options.DefaultTimeoutMs;
        }
        return requested > hardMax ? hardMax : requested;
    }

    private static string HashParams(JsonElement? p)
    {
        var json = p is null ? "null" : JsonSerializer.Serialize(p, Json);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }

    private static string? ReadStringParam(JsonElement? p, string name)
    {
        if (p is not { ValueKind: JsonValueKind.Object } root) return null;
        if (!root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
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
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "action";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }

    private Task WriteCaps(
        HttpContext ctx, JsonElement? payload, string? anchorRef,
        string? requestId, uint tokenEst = 0)
    {
        var dataList = payload is null
            ? Array.Empty<JsonElement>()
            : new[] { payload.Value };

        var caps = new CapsFrame
        {
            AnchorRef = anchorRef,
            Count     = (uint)dataList.Length,
            Data      = dataList,
            TokenEst  = tokenEst,
        };

        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = NwpHttpHeaders.MimeCapsule;
        ctx.Response.Headers[NwpHttpHeaders.NodeType] = "action";
        if (anchorRef is not null)
            ctx.Response.Headers[NwpHttpHeaders.Schema] = anchorRef;
        if (tokenEst > 0)
            ctx.Response.Headers[NwpHttpHeaders.Tokens] = tokenEst.ToString();
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

    private static (string nwmJson, string actionsJson) BuildStaticPayloads(ActionNodeOptions opt)
    {
        var baseUrl = opt.PathPrefix.TrimEnd('/');

        var nwm = new NeuralWebManifest
        {
            Nwp             = "0.4",
            NodeId          = opt.NodeId,
            NodeType        = "action",
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
                Required     = opt.RequireAuth,
                IdentityType = opt.RequireAuth ? "nip-cert" : "none",
            },
            Endpoints = new NodeEndpoints
            {
                Invoke = $"{baseUrl}/invoke",
                Schema = $"{baseUrl}/.schema",
            },
        };
        var nwmJson = JsonSerializer.Serialize(nwm, Json);

        // /actions: `{ "actions": { "orders.create": { ... }, ... } }` — exposes the
        // declared action registry so Agents can introspect without re-reading the NWM.
        var actionsJson = JsonSerializer.Serialize(
            new { actions = opt.Actions }, Json);

        return (nwmJson, actionsJson);
    }
}
