// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.Bridge;

/// <summary>ASP.NET-hosted Bridge Node options.</summary>
public sealed class BridgeNodeOptions
{
    /// <summary>Bridge Node identifier surfaced in <c>/.nwm</c>.</summary>
    public string NodeId { get; set; } = "nps-bridge";

    /// <summary>Path prefix for the Bridge Node endpoints. Empty string means root.</summary>
    public string PathPrefix { get; set; } = string.Empty;

    /// <summary>Action id accepted by <c>/invoke</c>.</summary>
    public string ActionId { get; set; } = "bridge.dispatch";

    /// <summary>Require the <c>X-NWP-Agent</c> header before dispatching.</summary>
    public bool RequireAuth { get; set; }

    /// <summary>Register HTTP/HTTPS, MCP JSON-RPC, and A2A JSON-RPC dispatchers automatically.</summary>
    public bool RegisterBuiltInDispatchers { get; set; } = true;
}
