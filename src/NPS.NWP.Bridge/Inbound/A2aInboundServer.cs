// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core;

namespace NPS.NWP.Bridge;

/// <summary>
/// Inbound A2A server surface of a Bridge Node (NWP §2.1 inbound profile, §16.1.2).
/// Projects the Action / Complex Nodes behind one or more <see cref="INwpBackend"/> instances
/// onto A2A skills. Supersedes <c>A2aServerBridge</c>, which could only reach a co-hosted node.
/// </summary>
public sealed class A2aInboundServer
{
    private readonly BridgeInboundOptions _options;
    private readonly IReadOnlyList<INwpBackend> _backends;

    /// <summary>Create an inbound A2A server over the given backends.</summary>
    public A2aInboundServer(BridgeInboundOptions options, IReadOnlyList<INwpBackend> backends)
    {
        _options = options;
        _backends = backends;
    }

    /// <summary>Build the A2A AgentCard advertising every skill this Bridge Node fronts.</summary>
    public async Task<A2aAgentCard> BuildAgentCardAsync(
        string endpointUrl,
        CancellationToken cancellationToken = default)
    {
        var skills = new List<A2aAgentSkill>();

        foreach (var (backend, descriptor) in await InvokableBackendsAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var action in await backend.GetActionsAsync(cancellationToken).ConfigureAwait(false))
            {
                skills.Add(new A2aAgentSkill
                {
                    Id = McpToolName.Encode(descriptor.Name, action.ActionId),
                    Name = action.Description ?? action.ActionId,
                    Description = action.Description,
                    Tags = action.Tags,
                    InputModes = ["text", "data"],
                    OutputModes = ["data"],
                });
            }
        }

        return new A2aAgentCard
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
                    Schemes = ["apikey"],
                    Credentials = "X-NWP-Agent",
                }
                : null,
            Skills = skills,
        };
    }

    /// <summary>Dispatch one A2A JSON-RPC request.</summary>
    public async Task<BridgeJsonRpcResponse> DispatchAsync(
        BridgeJsonRpcRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // §16.1.2 MUST-5: reject a protocol this Bridge Node did not declare in
        // bridge_inbound_protocols. (NPS-CR-0010 review finding F3)
        if (!_options.ServesInbound("a2a"))
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeErrorMap.ToJsonRpc(NpsStatusCodes.ServerUnsupported),
                "This Bridge Node does not declare \"a2a\" in bridge_inbound_protocols.",
                new { error = BridgeErrorCodes.DirectionUnsupported });
        }

        return request.Method switch
        {
            "tasks/send" => await SendTaskAsync(request, cancellationToken).ConfigureAwait(false),
            _ => BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.MethodNotFound,
                $"A2A method '{request.Method}' is not supported by this Bridge Node.",
                new { error = BridgeErrorCodes.DirectionUnsupported }),
        };
    }

    private async Task<BridgeJsonRpcResponse> SendTaskAsync(
        BridgeJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        A2aSendTaskParams? task;
        try
        {
            task = request.Params?.Deserialize<A2aSendTaskParams>(BridgeJsonRpc.Json);
        }
        catch (JsonException ex)
        {
            return BridgeJsonRpc.Error(request, BridgeJsonRpcErrorCodes.InvalidParams, ex.Message);
        }

        if (task is null || string.IsNullOrWhiteSpace(task.Id))
        {
            return BridgeJsonRpc.Error(
                request, BridgeJsonRpcErrorCodes.InvalidParams, "A2A tasks/send params.id is required.");
        }

        var resolved = await ResolveActionAsync(task, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.InvalidParams,
                "A2A task metadata must identify an exposed NPS action when more than one is available.",
                new { error = BridgeErrorCodes.ServerToolNotFound });
        }

        var (backend, action) = resolved.Value;

        var result = await backend
            .InvokeAsync(action.ActionId, ExtractActionParams(task), action.Async, cancellationToken)
            .ConfigureAwait(false);

        // §16.3: an auth / limit / unsupported failure MUST surface as a protocol-level error.
        // Reporting it as a *task* — even a failed one — hands the peer agent a task object where
        // it should have received a transport error, and A2A peers retry failed tasks.
        if (!result.Ok && BridgeErrorMap.MustBeProtocolError(result.NpsStatus))
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeErrorMap.ToJsonRpc(result.NpsStatus),
                result.Message ?? result.NpsStatus ?? "NWP dispatch failed.",
                new { status = result.NpsStatus, error = result.NwpError });
        }

        return BridgeJsonRpc.Success(request, JsonSerializer.SerializeToElement(
            ToTask(task, result), BridgeJsonRpc.Json));
    }

    // ── Resolution ───────────────────────────────────────────────────────────

    private async Task<(INwpBackend Backend, NwpActionDescriptor Action)?> ResolveActionAsync(
        A2aSendTaskParams task,
        CancellationToken cancellationToken)
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
                if (!string.IsNullOrWhiteSpace(requested)) break;
            }
        }

        var candidates = new List<(INwpBackend Backend, NwpActionDescriptor Action)>();

        foreach (var (backend, descriptor) in await InvokableBackendsAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var action in await backend.GetActionsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(requested))
                {
                    candidates.Add((backend, action));
                    continue;
                }

                var encoded = McpToolName.Encode(descriptor.Name, action.ActionId);
                if (string.Equals(encoded, requested, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(action.ActionId, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return (backend, action);
                }
            }
        }

        // No skill named: only unambiguous when exactly one action is exposed in total.
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private async Task<IReadOnlyList<(INwpBackend Backend, NwpNodeDescriptor Descriptor)>>
        InvokableBackendsAsync(CancellationToken cancellationToken)
    {
        var list = new List<(INwpBackend, NwpNodeDescriptor)>();

        foreach (var backend in _backends)
        {
            var descriptor = await backend.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
            if (descriptor.IsInvokable) list.Add((backend, descriptor));
        }

        return list;
    }

    // ── Projection ───────────────────────────────────────────────────────────

    private static A2aTask ToTask(A2aSendTaskParams request, NwpResult result)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("O");

        var payload = result.Ok
            ? result.Payload ?? JsonSerializer.SerializeToElement(new { }, BridgeJsonRpc.Json)
            : JsonSerializer.SerializeToElement(
                new { status = result.NpsStatus, error = result.NwpError, message = result.Message },
                BridgeJsonRpc.Json);

        return new A2aTask
        {
            Id = request.Id,
            SessionId = request.SessionId,
            Status = new A2aTaskStatus
            {
                State = result.Ok ? A2aTaskState.Completed : A2aTaskState.Failed,
                Timestamp = timestamp,
                Message = result.Ok
                    ? null
                    : new A2aMessage
                    {
                        Role = "agent",
                        Parts =
                        [
                            new A2aPart
                            {
                                Type = "text",
                                // §16.3: the NPS code is preserved verbatim in the failure detail.
                                Text = result.Message ?? result.NwpError ?? result.NpsStatus
                                       ?? "NPS action failed.",
                            },
                        ],
                    },
            },
            Artifacts =
            [
                new A2aArtifact
                {
                    Name = result.Ok ? "nps-result" : "nps-error",
                    Parts = [new A2aPart { Type = "data", Data = payload }],
                    Index = 0,
                },
            ],
            History = [request.Message],
        };
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static JsonElement? ExtractActionParams(A2aSendTaskParams task)
    {
        var fromMetadata = TryGetElement(task.Metadata, "params", "arguments") ??
                           TryGetElement(task.Message.Metadata, "params", "arguments");
        if (fromMetadata.HasValue) return fromMetadata.Value.Clone();

        foreach (var part in task.Message.Parts)
        {
            var nested = TryGetElement(part.Data, "params", "arguments");
            if (nested.HasValue) return nested.Value.Clone();

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

    private static string? TryGetString(JsonElement? source, params string[] names)
    {
        var value = TryGetElement(source, names);
        return value.HasValue && value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString()
            : null;
    }

    private static JsonElement? TryGetElement(JsonElement? source, params string[] names)
    {
        if (!source.HasValue || source.Value.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in names)
        {
            if (source.Value.TryGetProperty(name, out var value)) return value;
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
