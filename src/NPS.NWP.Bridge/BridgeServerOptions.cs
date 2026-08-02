// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Http;

namespace NPS.NWP.Bridge;

/// <summary>
/// Per-request verifier for inbound Bridge server callers.
///
/// <para>
/// This takes the full <see cref="HttpContext"/> on purpose. Binding <c>X-NWP-Agent</c> to a NIP
/// certificate needs the client certificate off the connection, so a transport-neutral header bag
/// would not be enough — which is precisely why this delegate, and the options type carrying it,
/// live in the hosting package rather than the core one.
/// </para>
/// </summary>
public delegate ValueTask<bool> BridgeServerAgentVerifier(
    string agentNid,
    HttpContext context,
    CancellationToken cancellationToken = default);

/// <summary>
/// ASP.NET Core hosting options for a Bridge Node's inbound surface. Extends the
/// transport-independent <see cref="BridgeInboundOptions"/> (backends, actions, declared inbound
/// protocols) with everything that only makes sense inside a web host: paths, auth, and limits.
/// </summary>
public sealed class BridgeServerOptions : BridgeInboundOptions
{
    /// <summary>Path prefix for inbound Bridge server endpoints. Empty string means root.</summary>
    public string PathPrefix { get; set; } = string.Empty;

    /// <summary>MCP HTTP endpoint under <see cref="PathPrefix"/>.</summary>
    public string McpPath { get; set; } = "/mcp";

    /// <summary>A2A JSON-RPC endpoint under <see cref="PathPrefix"/>.</summary>
    public string A2aPath { get; set; } = "/a2a";

    /// <summary>A2A AgentCard endpoint under <see cref="PathPrefix"/>.</summary>
    public string A2aAgentCardPath { get; set; } = "/.well-known/agent.json";

    /// <summary>
    /// Verifier for deployments that bind <c>X-NWP-Agent</c> to NIP certs, signatures,
    /// capabilities, reputation, or rate-limit policy. Required when <see cref="RequireAuth"/> is
    /// <c>true</c>.
    /// </summary>
    public BridgeServerAgentVerifier? VerifyAgentAsync { get; set; }

    /// <summary>Maximum inbound JSON-RPC request body size in bytes. Set to 0 to disable.</summary>
    public long MaxRequestBodyBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>Maximum time allowed for inbound dispatch. Set to 0 to disable.</summary>
    public uint DispatchTimeoutMs { get; set; } = 30_000;
}
