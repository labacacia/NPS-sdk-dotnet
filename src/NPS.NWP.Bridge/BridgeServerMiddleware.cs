// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPS.NWP.Http;

namespace NPS.NWP.Bridge;

/// <summary>ASP.NET Core middleware exposing inbound MCP/A2A Bridge server adapters.</summary>
public sealed class BridgeServerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly McpInboundServer _mcp;
    private readonly A2aInboundServer _a2a;
    private readonly BridgeServerOptions _options;
    private readonly ILogger _logger;

    /// <summary>Create Bridge server middleware.</summary>
    public BridgeServerMiddleware(
        RequestDelegate next,
        McpInboundServer mcp,
        A2aInboundServer a2a,
        BridgeServerOptions options,
        ILogger<BridgeServerMiddleware> logger)
    {
        _next = next;
        _mcp = mcp;
        _a2a = a2a;
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
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        var sub = path[prefix.Length..];
        if (Matches(sub, _options.McpPath) || Matches(sub, Append(_options.McpPath, "/sse")))
        {
            await HandleMcpAsync(ctx, useSse: IsSseRequest(ctx) || Matches(sub, Append(_options.McpPath, "/sse")))
                .ConfigureAwait(false);
            return;
        }

        if (Matches(sub, _options.A2aPath))
        {
            await HandleA2aAsync(ctx).ConfigureAwait(false);
            return;
        }

        if (Matches(sub, _options.A2aAgentCardPath))
        {
            await HandleAgentCardAsync(ctx).ConfigureAwait(false);
            return;
        }

        await _next(ctx).ConfigureAwait(false);
    }

    private async Task HandleMcpAsync(HttpContext ctx, bool useSse)
    {
        if (ctx.Request.Method == HttpMethods.Get && useSse)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync($"event: endpoint\ndata: {Join(_options.PathPrefix, _options.McpPath)}\n\n",
                ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (ctx.Request.Method != HttpMethods.Post)
        {
            ctx.Response.StatusCode = 405;
            return;
        }

        var auth = await AuthorizeAsync(ctx).ConfigureAwait(false);
        if (!auth.Authorized)
        {
            await WriteJsonRpcError(ctx, 401, BridgeJsonRpcErrorCodes.InvalidRequest,
                    auth.Message)
                .ConfigureAwait(false);
            return;
        }

        var result = await ReadAndDispatchAsync(ctx, _mcp.DispatchAsync).ConfigureAwait(false);
        if (useSse)
            await WriteSse(ctx, result.Response, result.HttpStatus).ConfigureAwait(false);
        else
            await WriteJson(ctx, result.HttpStatus, result.Response).ConfigureAwait(false);
    }

    private async Task HandleA2aAsync(HttpContext ctx)
    {
        if (ctx.Request.Method != HttpMethods.Post)
        {
            ctx.Response.StatusCode = 405;
            return;
        }

        var auth = await AuthorizeAsync(ctx).ConfigureAwait(false);
        if (!auth.Authorized)
        {
            await WriteJsonRpcError(ctx, 401, BridgeJsonRpcErrorCodes.InvalidRequest,
                    auth.Message)
                .ConfigureAwait(false);
            return;
        }

        var result = await ReadAndDispatchAsync(ctx, _a2a.DispatchAsync).ConfigureAwait(false);
        await WriteJson(ctx, result.HttpStatus, result.Response).ConfigureAwait(false);
    }

    private async Task HandleAgentCardAsync(HttpContext ctx)
    {
        if (ctx.Request.Method != HttpMethods.Get)
        {
            ctx.Response.StatusCode = 405;
            return;
        }

        var endpoint = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{Join(_options.PathPrefix, _options.A2aPath)}";
        var card = await _a2a.BuildAgentCardAsync(endpoint, ctx.RequestAborted).ConfigureAwait(false);
        await WriteJson(ctx, 200, card).ConfigureAwait(false);
    }

    private async Task<BridgeHttpResult> ReadAndDispatchAsync(
        HttpContext ctx,
        Func<BridgeJsonRpcRequest, CancellationToken, Task<BridgeJsonRpcResponse>> dispatch)
    {
        try
        {
            var request = await ReadJsonRpcRequestAsync(ctx).ConfigureAwait(false);
            if (request is null)
            {
                return BridgeHttpResult.BadRequest(BridgeJsonRpc.Error(
                    (JsonElement?)null,
                    BridgeJsonRpcErrorCodes.InvalidRequest,
                    "JSON-RPC request is required."));
            }

            return new BridgeHttpResult(
                200,
                await DispatchWithTimeoutAsync(ctx, request, dispatch).ConfigureAwait(false));
        }
        catch (BridgePayloadTooLargeException ex)
        {
            return new BridgeHttpResult(
                StatusCodes.Status413PayloadTooLarge,
                BridgeJsonRpc.Error((JsonElement?)null, BridgeJsonRpcErrorCodes.InvalidRequest, ex.Message));
        }
        catch (BridgeDispatchTimeoutException ex)
        {
            return new BridgeHttpResult(
                StatusCodes.Status504GatewayTimeout,
                BridgeJsonRpc.Error((JsonElement?)null, BridgeJsonRpcErrorCodes.UpstreamError, ex.Message));
        }
        catch (JsonException ex)
        {
            return BridgeHttpResult.BadRequest(
                BridgeJsonRpc.Error((JsonElement?)null, BridgeJsonRpcErrorCodes.ParseError, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bridge server request failed.");
            return new BridgeHttpResult(
                500,
                BridgeJsonRpc.Error(
                (JsonElement?)null,
                BridgeJsonRpcErrorCodes.InternalError,
                "Bridge server request failed."));
        }
    }

    private async Task<BridgeJsonRpcRequest?> ReadJsonRpcRequestAsync(HttpContext ctx)
    {
        var maxBytes = _options.MaxRequestBodyBytes;
        if (maxBytes > 0 && ctx.Request.ContentLength is { } contentLength && contentLength > maxBytes)
            throw new BridgePayloadTooLargeException(maxBytes);

        if (maxBytes <= 0)
        {
            return await JsonSerializer.DeserializeAsync<BridgeJsonRpcRequest>(
                ctx.Request.Body, BridgeJsonRpc.Json, ctx.RequestAborted).ConfigureAwait(false);
        }

        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted).ConfigureAwait(false);
            if (read == 0)
                break;

            if (buffer.Length + read > maxBytes)
                throw new BridgePayloadTooLargeException(maxBytes);

            await buffer.WriteAsync(chunk.AsMemory(0, read), ctx.RequestAborted).ConfigureAwait(false);
        }

        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<BridgeJsonRpcRequest>(
            buffer, BridgeJsonRpc.Json, ctx.RequestAborted).ConfigureAwait(false);
    }

    private async Task<BridgeJsonRpcResponse> DispatchWithTimeoutAsync(
        HttpContext ctx,
        BridgeJsonRpcRequest request,
        Func<BridgeJsonRpcRequest, CancellationToken, Task<BridgeJsonRpcResponse>> dispatch)
    {
        if (_options.DispatchTimeoutMs == 0)
            return await dispatch(request, ctx.RequestAborted).ConfigureAwait(false);

        using var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var dispatchTask = dispatch(request, dispatchCts.Token);
        var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(_options.DispatchTimeoutMs), ctx.RequestAborted);

        var completed = await Task.WhenAny(dispatchTask, timeoutTask).ConfigureAwait(false);
        if (completed == dispatchTask)
            return await dispatchTask.ConfigureAwait(false);

        if (ctx.RequestAborted.IsCancellationRequested)
            throw new OperationCanceledException(ctx.RequestAborted);

        await dispatchCts.CancelAsync().ConfigureAwait(false);
        _ = dispatchTask.ContinueWith(
            task => _logger.LogError(task.Exception, "Bridge server dispatch failed after timeout."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        throw new BridgeDispatchTimeoutException(_options.DispatchTimeoutMs);
    }

    private async ValueTask<BridgeAuthResult> AuthorizeAsync(HttpContext ctx)
    {
        if (!_options.RequireAuth)
            return BridgeAuthResult.Allow;

        if (!ctx.Request.Headers.TryGetValue(NwpHttpHeaders.Agent, out var values) ||
            values.Count != 1 ||
            string.IsNullOrWhiteSpace(values[0]))
        {
            return BridgeAuthResult.Deny("A valid X-NWP-Agent NID is required.");
        }

        var agentNid = values[0]!.Trim();
        if (!IsValidAgentNid(agentNid))
            return BridgeAuthResult.Deny("A valid X-NWP-Agent NID is required.");

        if (_options.VerifyAgentAsync is null)
            return BridgeAuthResult.Deny("Bridge server agent verifier is required.");

        if (!await _options.VerifyAgentAsync(agentNid, ctx, ctx.RequestAborted).ConfigureAwait(false))
        {
            return BridgeAuthResult.Deny("X-NWP-Agent was rejected by Bridge server policy.");
        }

        return BridgeAuthResult.Allow;
    }

    private static bool IsValidAgentNid(string nid)
    {
        const string prefix = "urn:nps:agent:";
        if (!nid.StartsWith(prefix, StringComparison.Ordinal) || nid.Length > 512)
            return false;

        var rest = nid[prefix.Length..];
        var sep = rest.IndexOf(':');
        if (sep <= 0 || sep == rest.Length - 1)
            return false;

        var domain = rest[..sep];
        var identifier = rest[(sep + 1)..];
        return domain.All(IsDomainChar) && identifier.All(IsIdentifierChar);
    }

    private static bool IsDomainChar(char ch) =>
        char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-';

    private static bool IsIdentifierChar(char ch) =>
        char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or '~' or ':' or '@' or '/';

    private readonly record struct BridgeAuthResult(bool Authorized, string Message)
    {
        public static readonly BridgeAuthResult Allow = new(true, string.Empty);
        public static BridgeAuthResult Deny(string message) => new(false, message);
    }

    private static Task WriteJson(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(ctx.Response.Body, body, BridgeJsonRpc.Json, ctx.RequestAborted);
    }

    private static Task WriteJsonRpcError(HttpContext ctx, int status, int code, string message) =>
        WriteJson(ctx, status, BridgeJsonRpc.Error((JsonElement?)null, code, message));

    private static async Task WriteSse(HttpContext ctx, BridgeJsonRpcResponse response, int status)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/event-stream";
        var payload = JsonSerializer.Serialize(response, BridgeJsonRpc.Json);
        await ctx.Response.WriteAsync($"event: message\ndata: {payload}\n\n", ctx.RequestAborted)
            .ConfigureAwait(false);
    }

    private static bool Matches(string actual, string expected)
    {
        var normalized = expected.StartsWith('/') ? expected : "/" + expected;
        return actual.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
               actual.Equals(normalized + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Append(string path, string suffix) =>
        path.TrimEnd('/') + suffix;

    private static string Join(string prefix, string path)
    {
        var left = prefix.TrimEnd('/');
        var right = path.StartsWith('/') ? path : "/" + path;
        return string.IsNullOrEmpty(left) ? right : left + right;
    }

    private static bool IsSseRequest(HttpContext ctx) =>
        ctx.Request.Headers.Accept.Any(value =>
            value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);

    private sealed record BridgeHttpResult(int HttpStatus, BridgeJsonRpcResponse Response)
    {
        public static BridgeHttpResult BadRequest(BridgeJsonRpcResponse response) =>
            new(StatusCodes.Status400BadRequest, response);
    }

    private sealed class BridgePayloadTooLargeException : Exception
    {
        public BridgePayloadTooLargeException(long maxBytes)
            : base($"Bridge server request body exceeds the configured {maxBytes} byte limit.")
        {
        }
    }

    private sealed class BridgeDispatchTimeoutException : Exception
    {
        public BridgeDispatchTimeoutException(uint timeoutMs)
            : base($"Bridge server dispatch timed out after {timeoutMs}ms.")
        {
        }
    }
}
