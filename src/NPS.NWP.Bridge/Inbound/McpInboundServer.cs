// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core;

namespace NPS.NWP.Bridge;

/// <summary>
/// Inbound MCP server surface of a Bridge Node (NWP §2.1 inbound profile, §16.1.2).
/// Projects the NWP nodes behind one or more <see cref="INwpBackend"/> instances onto MCP:
/// Memory / Complex Nodes become resources, Action / Complex Nodes become tools.
///
/// <para>
/// Supersedes both pre-CR-0010 inbound implementations — the SDK's <c>McpServerBridge</c>
/// (in-process, action-only, no <c>resources/*</c>) and <c>compat/mcp-ingress</c> (HTTP,
/// full method set). It serves the full method set over either backend shape, and unlike
/// both predecessors it maps NPS failures onto JSON-RPC errors per §16.3 instead of
/// returning them as a "successful" result carrying <c>isError: true</c>.
/// </para>
/// </summary>
public sealed class McpInboundServer
{
    /// <summary>MCP methods a conformant inbound Bridge Node MUST serve (NWP §16.1.2).</summary>
    public static readonly IReadOnlyList<string> RequiredMethods =
        ["initialize", "ping", "tools/list", "tools/call", "resources/list", "resources/read"];

    private readonly BridgeInboundOptions _options;
    private readonly IReadOnlyList<INwpBackend> _backends;

    /// <summary>Create an inbound MCP server over the given backends.</summary>
    public McpInboundServer(BridgeInboundOptions options, IReadOnlyList<INwpBackend> backends)
    {
        _options = options;
        _backends = backends;
    }

    /// <summary>Dispatch one MCP JSON-RPC request.</summary>
    public async Task<BridgeJsonRpcResponse> DispatchAsync(
        BridgeJsonRpcRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // §16.1.2 MUST-5: reject a protocol this Bridge Node did not declare in
        // bridge_inbound_protocols, rather than serving it anyway. (NPS-CR-0010 review finding F3)
        if (!_options.ServesInbound("mcp"))
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeErrorMap.ToJsonRpc(NpsStatusCodes.ServerUnsupported),
                "This Bridge Node does not declare \"mcp\" in bridge_inbound_protocols.",
                new { error = BridgeErrorCodes.DirectionUnsupported });
        }

        try
        {
            return request.Method switch
            {
                "initialize" => BridgeJsonRpc.Success(request, Initialize()),
                "ping" => BridgeJsonRpc.Success(request, EmptyObject()),
                "tools/list" => BridgeJsonRpc.Success(request, await ListToolsAsync(cancellationToken)),
                "tools/call" => await CallToolAsync(request, cancellationToken).ConfigureAwait(false),
                "resources/list" => BridgeJsonRpc.Success(request, await ListResourcesAsync(cancellationToken)),
                "resources/read" => await ReadResourceAsync(request, cancellationToken).ConfigureAwait(false),
                _ => BridgeJsonRpc.Error(
                    request,
                    BridgeJsonRpcErrorCodes.MethodNotFound,
                    $"MCP method '{request.Method}' is not supported by this Bridge Node.",
                    new { error = BridgeErrorCodes.DirectionUnsupported }),
            };
        }
        catch (JsonException ex)
        {
            return BridgeJsonRpc.Error(request, BridgeJsonRpcErrorCodes.InvalidParams, ex.Message);
        }
    }

    // ── initialize / ping ────────────────────────────────────────────────────

    private JsonElement Initialize() => JsonSerializer.SerializeToElement(
        new McpInitializeResult
        {
            ServerInfo = new McpServerInfo
            {
                Name = _options.ServerName,
                Version = _options.ServerVersion,
            },
            // Both capabilities are always advertised: §16.1.2 requires the resource *methods*
            // to be served even when no Memory Node sits behind this Bridge.
            Capabilities = new McpServerCapabilities
            {
                Tools = new McpToolCapabilities(),
                Resources = new McpResourceCapabilities(),
            },
        },
        BridgeJsonRpc.Json);

    private static JsonElement EmptyObject() =>
        JsonSerializer.SerializeToElement(new { }, BridgeJsonRpc.Json);

    // ── tools/list ───────────────────────────────────────────────────────────

    private async Task<JsonElement> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<McpTool>();

        foreach (var backend in _backends)
        {
            var descriptor = await backend.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
            if (!descriptor.IsInvokable) continue;

            foreach (var action in await backend.GetActionsAsync(cancellationToken).ConfigureAwait(false))
            {
                tools.Add(new McpTool
                {
                    Name = McpToolName.Encode(descriptor.Name, action.ActionId),
                    Description = action.Description,
                    InputSchema = action.InputSchema?.Clone() ?? OpenObjectSchema(),
                });
            }
        }

        return JsonSerializer.SerializeToElement(
            new McpToolListResult { Tools = tools }, BridgeJsonRpc.Json);
    }

    // ── tools/call ───────────────────────────────────────────────────────────

    private async Task<BridgeJsonRpcResponse> CallToolAsync(
        BridgeJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var call = request.Params?.Deserialize<McpToolCallParams>(BridgeJsonRpc.Json);
        if (call is null || string.IsNullOrWhiteSpace(call.Name))
        {
            return BridgeJsonRpc.Error(
                request, BridgeJsonRpcErrorCodes.InvalidParams, "MCP tools/call requires params.name.");
        }

        var resolved = await ResolveToolAsync(call.Name, cancellationToken).ConfigureAwait(false);
        if (resolved is null) return ToolNotFound(request, call.Name);

        var (backend, action) = resolved.Value;

        var result = await backend
            .InvokeAsync(action.ActionId, call.Arguments, action.Async, cancellationToken)
            .ConfigureAwait(false);

        return ToToolCallResponse(request, result);
    }

    /// <summary>
    /// Resolve an MCP tool name to its backend and action.
    ///
    /// <para>
    /// Canonical on output, forgiving on input. <c>tools/list</c> always emits the qualified
    /// <c>node__action</c> form — it has to, since MCP tool names are a flat namespace and a Bridge
    /// may front several nodes. But a bare action id is also accepted when it resolves unambiguously,
    /// so MCP clients written against the pre-CR-0010 in-process Bridge (which emitted unqualified
    /// names) keep working.
    /// </para>
    /// </summary>
    private async Task<(INwpBackend Backend, NwpActionDescriptor Action)?> ResolveToolAsync(
        string toolName,
        CancellationToken cancellationToken)
    {
        var unqualified = new List<(INwpBackend, NwpActionDescriptor)>();

        foreach (var backend in _backends)
        {
            var descriptor = await backend.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
            if (!descriptor.IsInvokable) continue;

            foreach (var action in await backend.GetActionsAsync(cancellationToken).ConfigureAwait(false))
            {
                // Match by re-encoding, never by decoding the incoming name. Encoding is lossy
                // (dots fold to underscores; `__` can appear inside a node name), so a decode that
                // tried to invert it would mis-split `node__a_b` into action `a.b`, or misparse a
                // node whose own name contains `__`. Comparing Encode(node, action) == toolName
                // sidesteps all of that — whatever tools/list emitted for this action is exactly
                // what we compare against. (NPS-CR-0010 review finding F1)
                if (string.Equals(
                        McpToolName.Encode(descriptor.Name, action.ActionId),
                        toolName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return (backend, action);
                }

                // Bare-name fallback for clients written against the pre-CR-0010 in-process Bridge,
                // which emitted unqualified names: match the raw action id or the encoded action
                // segment (dots folded), without the node prefix.
                if (string.Equals(action.ActionId, toolName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(McpToolName.EncodeActionSegment(action.ActionId), toolName, StringComparison.OrdinalIgnoreCase))
                {
                    unqualified.Add((backend, action));
                }
            }
        }

        // Only accept the fallback when it is unambiguous — two nodes exposing the same action id
        // must be disambiguated by the caller, not guessed at here.
        return unqualified.Count == 1 ? unqualified[0] : null;
    }

    /// <summary>
    /// Serve MCP over stdio: line-delimited JSON-RPC in, line-delimited JSON-RPC out.
    /// This is the transport most MCP clients launch a server with, so it is part of the
    /// inbound profile, not an extra.
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
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            BridgeJsonRpcResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<BridgeJsonRpcRequest>(line, BridgeJsonRpc.Json);
                response = request is null
                    ? BridgeJsonRpc.Error(
                        (JsonElement?)null,
                        BridgeJsonRpcErrorCodes.InvalidRequest,
                        "JSON-RPC request is required.")
                    : await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                response = BridgeJsonRpc.Error(
                    (JsonElement?)null, BridgeJsonRpcErrorCodes.ParseError, ex.Message);
            }

            await output
                .WriteLineAsync(JsonSerializer.Serialize(response, BridgeJsonRpc.Json))
                .ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Turn an NWP result into an MCP <c>tools/call</c> response.
    ///
    /// <para>
    /// The split here is the §16.3 rule that both predecessors got wrong. An auth, limit, or
    /// unsupported failure MUST surface as a JSON-RPC <i>error</i> — returning it as a successful
    /// result with <c>isError: true</c> lets an MCP client mistake a 403 for a tool that merely
    /// returned unhappy text. Ordinary domain failures (a validation error the tool itself
    /// reports) stay as <c>isError: true</c> content, which is what MCP intends that flag for.
    /// </para>
    /// </summary>
    private static BridgeJsonRpcResponse ToToolCallResponse(BridgeJsonRpcRequest request, NwpResult result)
    {
        if (!result.Ok && BridgeErrorMap.MustBeProtocolError(result.NpsStatus))
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeErrorMap.ToJsonRpc(result.NpsStatus),
                result.Message ?? result.NpsStatus ?? "NWP dispatch failed.",
                new { status = result.NpsStatus, error = result.NwpError });
        }

        var text = result.Ok
            ? result.Payload?.GetRawText() ?? "{}"
            : JsonSerializer.Serialize(
                new { status = result.NpsStatus, error = result.NwpError, message = result.Message },
                BridgeJsonRpc.Json);

        return BridgeJsonRpc.Success(request, JsonSerializer.SerializeToElement(
            new McpToolCallResult
            {
                IsError = !result.Ok,
                Content = [new McpContent { Type = "text", Text = text }],
            },
            BridgeJsonRpc.Json));
    }

    private static BridgeJsonRpcResponse ToolNotFound(BridgeJsonRpcRequest request, string toolName) =>
        BridgeJsonRpc.Error(
            request,
            // §16.3: an unknown tool is a missing *method* to an MCP client. The retired -32002
            // is deliberately not reused.
            BridgeJsonRpcErrorCodes.MethodNotFound,
            $"MCP tool '{toolName}' is not exposed by this Bridge Node.",
            new { error = BridgeErrorCodes.ServerToolNotFound, tool = toolName });

    // ── resources/list ───────────────────────────────────────────────────────

    private async Task<JsonElement> ListResourcesAsync(CancellationToken cancellationToken)
    {
        var resources = new List<McpResource>();

        foreach (var backend in _backends)
        {
            var descriptor = await backend.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
            if (!descriptor.IsQueryable) continue;

            resources.Add(new McpResource
            {
                Uri = $"nwp://{descriptor.Name}/",
                Name = descriptor.DisplayName ?? descriptor.Name,
                Description = descriptor.Description
                              ?? $"NWP {descriptor.Role} Node '{descriptor.Name}' — read to query.",
                MimeType = "application/json",
            });
        }

        // An empty set is conformant: §16.1.2 requires the method to be served, not that a
        // Memory Node exist behind it.
        return JsonSerializer.SerializeToElement(
            new McpResourceListResult { Resources = resources }, BridgeJsonRpc.Json);
    }

    // ── resources/read ───────────────────────────────────────────────────────

    private async Task<BridgeJsonRpcResponse> ReadResourceAsync(
        BridgeJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var read = request.Params?.Deserialize<McpResourceReadParams>(BridgeJsonRpc.Json);
        if (read is null || string.IsNullOrWhiteSpace(read.Uri))
        {
            return BridgeJsonRpc.Error(
                request, BridgeJsonRpcErrorCodes.InvalidParams, "MCP resources/read requires params.uri.");
        }

        if (!Uri.TryCreate(read.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "nwp")
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeJsonRpcErrorCodes.InvalidParams,
                $"Resource URI '{read.Uri}' must be of the form nwp://<node>/.");
        }

        var backend = await ResolveBackendAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        if (backend is null)
        {
            return BridgeJsonRpc.Error(
                request,
                // §16.3: an unknown *resource* is a bad argument, not a missing method.
                BridgeJsonRpcErrorCodes.InvalidParams,
                $"Resource '{read.Uri}' is not exposed by this Bridge Node.",
                new { error = BridgeErrorCodes.ServerToolNotFound, uri = read.Uri });
        }

        var query = JsonSerializer.SerializeToElement(
            new { limit = _options.ResourceReadLimit }, BridgeJsonRpc.Json);

        var result = await backend.QueryAsync(query, cancellationToken).ConfigureAwait(false);

        if (!result.Ok)
        {
            return BridgeJsonRpc.Error(
                request,
                BridgeErrorMap.ToJsonRpc(result.NpsStatus, resourceRead: true),
                result.Message ?? result.NpsStatus ?? "NWP query failed.",
                new { status = result.NpsStatus, error = result.NwpError });
        }

        return BridgeJsonRpc.Success(request, JsonSerializer.SerializeToElement(
            new McpResourceReadResult
            {
                Contents =
                [
                    new McpResourceContent
                    {
                        Uri = read.Uri,
                        MimeType = "application/json",
                        Text = result.Payload?.GetRawText() ?? "{}",
                    },
                ],
            },
            BridgeJsonRpc.Json));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<INwpBackend?> ResolveBackendAsync(string nodeName, CancellationToken cancellationToken)
    {
        foreach (var backend in _backends)
        {
            var descriptor = await backend.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(descriptor.Name, nodeName, StringComparison.OrdinalIgnoreCase))
                return backend;
        }

        return null;
    }

    private static JsonElement OpenObjectSchema() => JsonSerializer.SerializeToElement(
        new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = true,
        },
        BridgeJsonRpc.Json);
}

/// <summary>
/// Encodes an NWP <c>(node, action_id)</c> pair as a protocol-safe MCP tool name.
/// MCP tool names are a flat namespace, so the node name is folded in with a <c>__</c> separator;
/// dots in an action id — legal in NPS, awkward in some MCP clients — become underscores.
///
/// <para>
/// Encoding only. There is deliberately no decode: the transform is lossy (a dot and an underscore
/// both map to underscore; a node name may itself contain <c>__</c>), so an inverse would be
/// ambiguous. Callers resolve a tool name by <b>re-encoding</b> each candidate <c>(node, action)</c>
/// and comparing, never by splitting the incoming string. (NPS-CR-0010 review finding F1)
/// </para>
/// </summary>
public static class McpToolName
{
    private const string Separator = "__";

    /// <summary>Encode a node name and action id into one MCP tool name.</summary>
    public static string Encode(string nodeName, string actionId) =>
        $"{Sanitize(nodeName)}{Separator}{EncodeActionSegment(actionId)}";

    /// <summary>Encode just the action segment (no node prefix) — the bare form a pre-CR-0010 client saw.</summary>
    public static string EncodeActionSegment(string actionId) =>
        Sanitize(actionId).Replace('.', '_');

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "node";

        var chars = value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_')
            .ToArray();

        var name = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(name) ? "node" : name;
    }
}
