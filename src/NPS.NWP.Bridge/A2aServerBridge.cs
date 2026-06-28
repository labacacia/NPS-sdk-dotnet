// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>Inbound A2A adapter that exposes local NPS actions as A2A skills.</summary>
public sealed class A2aServerBridge
{
    private readonly BridgeServerOptions _options;
    private readonly IBridgeServerActionInvoker _invoker;

    /// <summary>Create an A2A server bridge.</summary>
    public A2aServerBridge(BridgeServerOptions options, IBridgeServerActionInvoker invoker)
    {
        _options = options;
        _invoker = invoker;
    }

    /// <summary>Build the A2A AgentCard for the hosted Bridge server.</summary>
    public A2aAgentCard BuildAgentCard(string endpointUrl) => new()
    {
        Name = _options.ServerName,
        Description = _options.Description,
        Url = endpointUrl,
        Provider = new A2aAgentProvider
        {
            Organization = "LabAcacia / INNO LOTUS PTY LTD",
            Url = "https://github.com/labacacia/nps",
        },
        Version = _options.ServerVersion,
        Capabilities = new A2aAgentCapabilities
        {
            Streaming = false,
            PushNotifications = false,
            StateTransitionHistory = false,
        },
        Authentication = _options.RequireAuth
            ? new A2aAgentAuthentication
            {
                Schemes = new[] { "apikey" },
                Credentials = "X-NWP-Agent",
            }
            : null,
        Skills = _options.Actions.Select(action => new A2aAgentSkill
        {
            Id = action.ActionId,
            Name = action.EffectiveDisplayName,
            Description = action.Description,
            Tags = action.Tags,
            InputModes = new[] { "text", "data" },
            OutputModes = new[] { "data" },
        }).ToArray(),
    };

    /// <summary>Dispatch one A2A JSON-RPC request.</summary>
    public async Task<BridgeJsonRpcResponse> DispatchAsync(
        BridgeJsonRpcRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Method switch
        {
            "tasks/send" => await SendTaskAsync(request, cancellationToken).ConfigureAwait(false),
            _ => BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.MethodNotFound,
                $"A2A method '{request.Method}' is not supported by NWP Bridge server."),
        };
    }

    private async Task<BridgeJsonRpcResponse> SendTaskAsync(
        BridgeJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Params.HasValue)
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.InvalidParams,
                "A2A tasks/send requires params.");
        }

        A2aSendTaskParams? task;
        try
        {
            task = request.Params.Value.Deserialize<A2aSendTaskParams>(BridgeJsonRpc.Json);
        }
        catch (JsonException ex)
        {
            return BridgeJsonRpc.Error(request, BridgeJsonRpcErrorCodes.InvalidParams, ex.Message);
        }

        if (task is null || string.IsNullOrWhiteSpace(task.Id))
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.InvalidParams,
                "A2A tasks/send params.id is required.");
        }

        var action = ResolveAction(task);
        if (action is null)
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.InvalidParams,
                "A2A task metadata must identify an exposed NPS action when multiple actions exist.",
                new { error = BridgeErrorCodes.ServerToolNotFound });
        }

        var frame = new ActionFrame
        {
            ActionId = action.ActionId,
            Params = ExtractActionParams(task),
            Async = action.Async,
            RequestId = task.Id,
        };

        try
        {
            var result = await _invoker.InvokeAsync(frame, cancellationToken).ConfigureAwait(false);
            return BridgeJsonRpc.Success(request, ToTask(task, result));
        }
        catch (Exception ex)
        {
            return BridgeJsonRpc.Success(request, ToTask(task, new ErrorFrame
            {
                Status = "NPS-SERVER-ERROR",
                Error = BridgeErrorCodes.ServerDispatchFailed,
                Message = ex.Message,
            }));
        }
    }

    private BridgeServerAction? ResolveAction(A2aSendTaskParams task)
    {
        var requested = FirstNonEmpty(
            TryGetString(task.Metadata, "action_id", "actionId", "skill_id", "skillId", "skill"),
            TryGetString(task.Message.Metadata, "action_id", "actionId", "skill_id", "skillId", "skill"));

        if (string.IsNullOrWhiteSpace(requested))
        {
            foreach (var part in task.Message.Parts)
            {
                requested = FirstNonEmpty(
                    TryGetString(part.Metadata, "action_id", "actionId", "skill_id", "skillId", "skill"),
                    TryGetString(part.Data, "action_id", "actionId", "skill_id", "skillId", "skill"));
                if (!string.IsNullOrWhiteSpace(requested))
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(requested) && _options.Actions.Count == 1)
            return _options.Actions[0];

        return _options.Actions.FirstOrDefault(action =>
            string.Equals(action.ActionId, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action.EffectiveToolName, requested, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement? ExtractActionParams(A2aSendTaskParams task)
    {
        var fromMetadata = TryGetElement(task.Metadata, "params", "arguments") ??
                           TryGetElement(task.Message.Metadata, "params", "arguments");
        if (fromMetadata.HasValue)
            return fromMetadata.Value.Clone();

        foreach (var part in task.Message.Parts)
        {
            var nested = TryGetElement(part.Data, "params", "arguments");
            if (nested.HasValue)
                return nested.Value.Clone();

            if (part.Type.Equals("data", StringComparison.OrdinalIgnoreCase) && part.Data.HasValue)
                return part.Data.Value.Clone();

            if (part.Type.Equals("text", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(part.Text))
            {
                return JsonSerializer.SerializeToElement(new { text = part.Text }, BridgeJsonRpc.Json);
            }
        }

        return null;
    }

    private static A2aTask ToTask(A2aSendTaskParams request, IFrame frame)
    {
        var isError = frame is ErrorFrame;
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var payload = BridgeFrameJson.ToElement(frame);

        return new A2aTask
        {
            Id = request.Id,
            SessionId = request.SessionId,
            Status = new A2aTaskStatus
            {
                State = isError ? A2aTaskState.Failed : A2aTaskState.Completed,
                Timestamp = timestamp,
                Message = isError
                    ? new A2aMessage
                    {
                        Role = "agent",
                        Parts = new[]
                        {
                            new A2aPart
                            {
                                Type = "text",
                                Text = frame is ErrorFrame error
                                    ? error.Message ?? error.Error
                                    : "NPS action failed.",
                            },
                        },
                    }
                    : null,
            },
            Artifacts = new[]
            {
                new A2aArtifact
                {
                    Name = isError ? "nps-error" : "nps-result",
                    Parts = new[]
                    {
                        new A2aPart
                        {
                            Type = "data",
                            Data = payload,
                        },
                    },
                    Index = 0,
                },
            },
            History = new[] { request.Message },
        };
    }

    private static string? TryGetString(JsonElement? source, params string[] names)
    {
        var value = TryGetElement(source, names);
        return value.HasValue && value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString()
            : null;
    }

    private static JsonElement? TryGetElement(JsonElement? source, params string[] names)
    {
        if (!source.HasValue || source.Value.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (source.Value.TryGetProperty(name, out var value))
                return value;
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
