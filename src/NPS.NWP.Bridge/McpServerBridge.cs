// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>Inbound MCP adapter that exposes local NPS actions as MCP tools.</summary>
public sealed class McpServerBridge
{
    private readonly BridgeServerOptions _options;
    private readonly IBridgeServerActionInvoker _invoker;

    /// <summary>Create an MCP server bridge.</summary>
    public McpServerBridge(BridgeServerOptions options, IBridgeServerActionInvoker invoker)
    {
        _options = options;
        _invoker = invoker;
    }

    /// <summary>Dispatch one MCP JSON-RPC request.</summary>
    public async Task<BridgeJsonRpcResponse> DispatchAsync(
        BridgeJsonRpcRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Method switch
        {
            "initialize" => BridgeJsonRpc.Success(request, Initialize()),
            "tools/list" => BridgeJsonRpc.Success(request, ListTools()),
            "tools/call" => await CallToolAsync(request, cancellationToken).ConfigureAwait(false),
            "ping" => BridgeJsonRpc.Success(request, new { }),
            _ => BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.MethodNotFound,
                $"MCP method '{request.Method}' is not supported by NWP Bridge server."),
        };
    }

    /// <summary>
    /// Run an MCP stdio loop. Each non-empty input line must contain one JSON-RPC request;
    /// each response is written as one JSON line.
    /// </summary>
    public async Task RunStdioAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            BridgeJsonRpcResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<BridgeJsonRpcRequest>(line, BridgeJsonRpc.Json);
                response = request is null
                    ? BridgeJsonRpc.Error((JsonElement?)null, BridgeJsonRpcErrorCodes.InvalidRequest, "JSON-RPC request is required.")
                    : await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                response = BridgeJsonRpc.Error((JsonElement?)null, BridgeJsonRpcErrorCodes.ParseError, ex.Message);
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(response, BridgeJsonRpc.Json).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private McpInitializeResult Initialize() => new()
    {
        ServerInfo = new McpServerInfo
        {
            Name = _options.ServerName,
            Version = _options.ServerVersion,
        },
        Capabilities = new McpServerCapabilities
        {
            Tools = new McpToolCapabilities { ListChanged = false },
        },
    };

    private McpToolListResult ListTools() => new()
    {
        Tools = _options.Actions.Select(action => new McpTool
        {
            Name = action.EffectiveToolName,
            Description = action.Description,
            InputSchema = action.InputSchema?.Clone() ?? DefaultInputSchema(),
        }).ToArray(),
    };

    private async Task<BridgeJsonRpcResponse> CallToolAsync(
        BridgeJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Params.HasValue)
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.InvalidParams,
                "MCP tools/call requires params.");
        }

        McpToolCallParams? call;
        try
        {
            call = request.Params.Value.Deserialize<McpToolCallParams>(BridgeJsonRpc.Json);
        }
        catch (JsonException ex)
        {
            return BridgeJsonRpc.Error(request, BridgeJsonRpcErrorCodes.InvalidParams, ex.Message);
        }

        if (call is null || string.IsNullOrWhiteSpace(call.Name))
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.InvalidParams,
                "MCP tools/call params.name is required.");
        }

        var action = ResolveAction(call.Name);
        if (action is null)
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.ToolNotFound,
                $"MCP tool '{call.Name}' is not exposed by NWP Bridge server.",
                new { error = BridgeErrorCodes.ServerToolNotFound, tool = call.Name });
        }

        var frame = new ActionFrame
        {
            ActionId = action.ActionId,
            Params = call.Arguments?.Clone(),
            Async = action.Async,
        };

        try
        {
            var result = await _invoker.InvokeAsync(frame, cancellationToken).ConfigureAwait(false);
            return BridgeJsonRpc.Success(request, ToToolResult(result));
        }
        catch (Exception ex)
        {
            return BridgeJsonRpc.Success(request, ToToolResult(new ErrorFrame
            {
                Status = "NPS-SERVER-ERROR",
                Error = BridgeErrorCodes.ServerDispatchFailed,
                Message = ex.Message,
            }));
        }
    }

    private BridgeServerAction? ResolveAction(string toolName) =>
        _options.Actions.FirstOrDefault(action =>
            string.Equals(action.EffectiveToolName, toolName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action.ActionId, toolName, StringComparison.OrdinalIgnoreCase));

    private static McpToolCallResult ToToolResult(IFrame frame)
    {
        var isError = frame is ErrorFrame;
        return new McpToolCallResult
        {
            IsError = isError,
            Content = new[]
            {
                new McpContent
                {
                    Type = "text",
                    Text = BridgeFrameJson.Serialize(frame),
                },
            },
        };
    }

    private static JsonElement DefaultInputSchema() =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = true,
        }, BridgeJsonRpc.Json);
}
