// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.Bridge;

/// <summary>
/// Built-in Bridge dispatcher for MCP JSON-RPC servers over HTTP POST.
/// </summary>
public sealed class McpBridgeDispatcher : JsonRpcBridgeDispatcher
{
    /// <summary>Anchor reference used for MCP bridge response records.</summary>
    public const string ResponseAnchorRef = "nps://bridge/mcp-jsonrpc-response/v1";

    /// <summary>Create an MCP bridge dispatcher over an existing client.</summary>
    public McpBridgeDispatcher(HttpClient client)
        : base(client, "tools/call", ResponseAnchorRef)
    {
    }

    /// <inheritdoc />
    public override string Protocol => BridgeProtocols.Mcp;
}
