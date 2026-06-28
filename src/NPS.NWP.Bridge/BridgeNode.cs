// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.Bridge;

/// <summary>
/// NWP Bridge Node — stateless translator between NPS frames and non-NPS
/// protocols (NPS-2 §2A, NPS-CR-0001). The package includes outbound
/// dispatchers for HTTP/HTTPS, gRPC JSON unary, MCP JSON-RPC, and A2A
/// JSON-RPC, plus inbound server adapters for exposing local NPS actions
/// to MCP and A2A clients. Additional outbound protocols can be registered
/// through <see cref="IBridgeDispatcher"/>.
/// <para>
/// Direction note: <see cref="BridgeNode"/> and <see cref="IBridgeDispatcher"/>
/// carry the <b>NPS → external</b> direction. <see cref="McpServerBridge"/>,
/// <see cref="A2aServerBridge"/>, and <see cref="BridgeServerMiddleware"/>
/// carry the inverse <b>external → NPS</b> direction.
/// </para>
/// </summary>
public static class BridgeNodeMetadata
{
    /// <summary>NWM <c>node_type</c> wire value for Bridge Node (NPS-2 §2A.1).</summary>
    public const string NodeType = "bridge";
}

/// <summary>
/// Wire string constants for the standard <c>bridge_protocols</c>
/// values declared in NDP <c>Announce</c> by Bridge Nodes
/// (NPS-4 §3.1, NPS-CR-0001 §3).
/// </summary>
public static class BridgeProtocols
{
    /// <summary>HTTP / HTTPS, REST + streaming.</summary>
    public const string Http = "http";

    /// <summary>gRPC, unary + streaming.</summary>
    public const string Grpc = "grpc";

    /// <summary>Model Context Protocol (Anthropic, et al.).</summary>
    public const string Mcp  = "mcp";

    /// <summary>Agent-to-Agent (Google A2A v0.2).</summary>
    public const string A2a  = "a2a";

    /// <summary>The full set of standard targets at v1.0-alpha.13.</summary>
    public static readonly IReadOnlyList<string> Standard = new[] { Http, Grpc, Mcp, A2a };

    /// <summary>Protocols with built-in dispatchers in this package.</summary>
    public static readonly IReadOnlyList<string> BuiltIn = new[] { Http, Grpc, Mcp, A2a };
}

/// <summary>
/// Descriptor for a Bridge Node deployment — declares which external
/// targets the deployment can reach. Used to populate
/// <c>Announce.bridge_protocols</c>; concrete dispatcher wiring is
/// the implementation's responsibility.
/// </summary>
/// <param name="Nid">The Bridge Node's NID.</param>
/// <param name="SupportedProtocols">
/// The set of protocol identifiers this Bridge can target. Each value
/// SHOULD be from <see cref="BridgeProtocols.Standard"/>; non-standard
/// values are allowed and travel as opaque strings.
/// </param>
public sealed record BridgeNodeDescriptor(
    string                  Nid,
    IReadOnlySet<string>    SupportedProtocols);

/// <summary>
/// Inbound parameter object — surfaces the <c>bridge_target</c> that
/// every Bridge invocation MUST carry. Concrete schema beyond
/// <see cref="Protocol"/> + <see cref="Endpoint"/> is per-protocol and
/// travels in <see cref="Extras"/> for adapter-specific options.
/// </summary>
/// <param name="Protocol">
/// One of <see cref="BridgeProtocols.Http"/>, <see cref="BridgeProtocols.Grpc"/>,
/// <see cref="BridgeProtocols.Mcp"/>, <see cref="BridgeProtocols.A2a"/>,
/// or any future-CR-registered identifier.
/// </param>
/// <param name="Endpoint">
/// Target endpoint URL or address; per-protocol semantics
/// (e.g. an HTTP URL, a gRPC <c>host:port/Service/Method</c> spec,
/// an MCP server URI).
/// </param>
/// <param name="Extras">
/// Additional protocol-specific knobs (HTTP method, headers,
/// gRPC metadata, MCP tool name, etc.). Implementation-defined for
/// the selected adapter.
/// </param>
public sealed record BridgeTarget(
    string                                          Protocol,
    string                                          Endpoint,
    IReadOnlyDictionary<string, object?>?           Extras = null);
