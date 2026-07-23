// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>Optional per-request verifier for inbound Bridge server callers.</summary>
public delegate ValueTask<bool> BridgeServerAgentVerifier(
    string agentNid,
    HttpContext context,
    CancellationToken cancellationToken = default);

/// <summary>Dispatch delegate used by inbound Bridge server adapters.</summary>
public delegate Task<IFrame> BridgeServerActionDispatcher(
    ActionFrame frame,
    CancellationToken cancellationToken = default);

/// <summary>Action exposed by inbound MCP/A2A Bridge server adapters.</summary>
public sealed record BridgeServerAction
{
    /// <summary>NPS action identifier dispatched to the local node.</summary>
    public required string ActionId { get; init; }

    /// <summary>Protocol-safe MCP tool name. Defaults to a sanitized <see cref="ActionId"/>.</summary>
    public string? ToolName { get; init; }

    /// <summary>Human-readable display name for A2A AgentCard entries.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Short action/tool description.</summary>
    public string? Description { get; init; }

    /// <summary>JSON Schema describing input arguments.</summary>
    public JsonElement? InputSchema { get; init; }

    /// <summary>Whether generated <see cref="ActionFrame"/> values should request async execution.</summary>
    public bool Async { get; init; }

    /// <summary>Optional A2A skill tags.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Effective MCP tool name for this action.</summary>
    public string EffectiveToolName =>
        string.IsNullOrWhiteSpace(ToolName) ? ToToolName(ActionId) : ToolName!;

    /// <summary>Effective display name for A2A AgentCard skills.</summary>
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? ActionId : DisplayName!;

    /// <summary>Return a protocol-safe MCP tool name for an NPS action id.</summary>
    public static string ToToolName(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId))
            return "action";

        var chars = actionId.Trim().Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray();
        var name = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(name) ? "action" : name;
    }
}

/// <summary>Options for inbound MCP/A2A Bridge server hosting.</summary>
public sealed class BridgeServerOptions
{
    /// <summary>Bridge server identifier surfaced in protocol metadata.</summary>
    public string NodeId { get; set; } = "nps-bridge-server";

    /// <summary>Path prefix for inbound Bridge server endpoints. Empty string means root.</summary>
    public string PathPrefix { get; set; } = string.Empty;

    /// <summary>MCP HTTP endpoint under <see cref="PathPrefix"/>.</summary>
    public string McpPath { get; set; } = "/mcp";

    /// <summary>A2A JSON-RPC endpoint under <see cref="PathPrefix"/>.</summary>
    public string A2aPath { get; set; } = "/a2a";

    /// <summary>A2A AgentCard endpoint under <see cref="PathPrefix"/>.</summary>
    public string A2aAgentCardPath { get; set; } = "/.well-known/agent.json";

    /// <summary>Require a valid <c>X-NWP-Agent</c> NID header before dispatching requests.</summary>
    public bool RequireAuth { get; set; } = true;

    /// <summary>
    /// Verifier for deployments that bind <c>X-NWP-Agent</c> to NIP certs,
    /// signatures, capabilities, reputation, or rate-limit policy. Required
    /// when <see cref="RequireAuth"/> is <c>true</c>.
    /// </summary>
    public BridgeServerAgentVerifier? VerifyAgentAsync { get; set; }

    /// <summary>Server name returned by MCP initialize and A2A AgentCard.</summary>
    public string ServerName { get; set; } = "nps-bridge-server";

    /// <summary>Server version returned by MCP initialize and A2A AgentCard.</summary>
    public string ServerVersion { get; set; } = "1.0.0-alpha.16";

    /// <summary>Server description returned by A2A AgentCard.</summary>
    public string? Description { get; set; } = "NPS Bridge server ingress.";

    /// <summary>Actions exposed as MCP tools and A2A skills.</summary>
    public IList<BridgeServerAction> Actions { get; } = new List<BridgeServerAction>();

    /// <summary>Local NPS action dispatcher used by inbound Bridge server adapters.</summary>
    public BridgeServerActionDispatcher? DispatchAsync { get; set; }

    /// <summary>Maximum inbound JSON-RPC request body size in bytes. Set to 0 to disable this middleware limit.</summary>
    public long MaxRequestBodyBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>Maximum time allowed for MCP/A2A dispatch. Set to 0 to disable this middleware timeout.</summary>
    public uint DispatchTimeoutMs { get; set; } = 30_000;

    /// <summary>Add an exposed local action and return these options for chaining.</summary>
    public BridgeServerOptions AddAction(
        string actionId,
        string? description = null,
        JsonElement? inputSchema = null,
        string? toolName = null,
        bool async = false,
        string? displayName = null,
        IReadOnlyList<string>? tags = null)
    {
        Actions.Add(new BridgeServerAction
        {
            ActionId = actionId,
            ToolName = toolName,
            DisplayName = displayName,
            Description = description,
            InputSchema = inputSchema,
            Async = async,
            Tags = tags,
        });
        return this;
    }
}

/// <summary>Invokes local NPS actions for inbound Bridge server adapters.</summary>
public interface IBridgeServerActionInvoker
{
    /// <summary>Invoke a local NPS action and return its frame response.</summary>
    Task<IFrame> InvokeAsync(ActionFrame frame, CancellationToken cancellationToken = default);
}

internal sealed class BridgeServerActionInvoker : IBridgeServerActionInvoker
{
    private readonly BridgeServerOptions _options;

    public BridgeServerActionInvoker(BridgeServerOptions options)
    {
        _options = options;
    }

    public async Task<IFrame> InvokeAsync(ActionFrame frame, CancellationToken cancellationToken = default)
    {
        if (_options.DispatchAsync is null)
        {
            return new ErrorFrame
            {
                Status = "NPS-SERVER-NOT-IMPLEMENTED",
                Error = BridgeErrorCodes.ServerDispatcherMissing,
                Message = "BridgeServerOptions.DispatchAsync must be configured before handling inbound Bridge calls.",
            };
        }

        return await _options.DispatchAsync(frame, cancellationToken).ConfigureAwait(false);
    }
}
