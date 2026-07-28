// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>
/// Translates one NWP action invocation into a concrete non-NPS protocol call.
/// </summary>
public interface IBridgeDispatcher
{
    /// <summary>Bridge protocol identifier served by this dispatcher.</summary>
    string Protocol { get; }

    /// <summary>Dispatch an action frame to the requested external target.</summary>
    Task<CapsFrame> DispatchAsync(
        ActionFrame frame,
        BridgeTarget target,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory registry mapping bridge protocol identifiers to dispatchers.
/// </summary>
public sealed class BridgeDispatcherRegistry
{
    private readonly Dictionary<string, IBridgeDispatcher> _dispatchers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Create an empty dispatcher registry.</summary>
    public BridgeDispatcherRegistry()
    {
    }

    /// <summary>Create a registry preloaded with dispatchers.</summary>
    public BridgeDispatcherRegistry(IEnumerable<IBridgeDispatcher> dispatchers)
    {
        foreach (var dispatcher in dispatchers)
            Register(dispatcher);
    }

    /// <summary>
    /// Create a registry with all built-in dispatchers: HTTP/HTTPS, gRPC JSON,
    /// MCP JSON-RPC, and A2A JSON-RPC.
    /// </summary>
    public static BridgeDispatcherRegistry CreateDefault(HttpClient client) =>
        new BridgeDispatcherRegistry()
            .Register(new HttpBridgeDispatcher(client))
            .Register(new GrpcBridgeDispatcher(client))
            .Register(new McpBridgeDispatcher(client))
            .Register(new A2aBridgeDispatcher(client));

    /// <summary>The currently registered protocol identifiers.</summary>
    public IReadOnlyCollection<string> Protocols => _dispatchers.Keys.ToArray();

    /// <summary>Register or replace the dispatcher for its protocol.</summary>
    public BridgeDispatcherRegistry Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (string.IsNullOrWhiteSpace(dispatcher.Protocol))
            throw new ArgumentException("Bridge dispatcher protocol must not be empty.", nameof(dispatcher));

        _dispatchers[dispatcher.Protocol] = dispatcher;
        return this;
    }

    /// <summary>Resolve a dispatcher for <paramref name="protocol"/>.</summary>
    public IBridgeDispatcher Resolve(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            throw new BridgeDispatchException(BridgeErrorCodes.TargetInvalid, "bridge_target.protocol is required.");

        return _dispatchers.TryGetValue(protocol, out var dispatcher)
            ? dispatcher
            : throw new BridgeDispatchException(
                BridgeErrorCodes.ProtocolUnsupported,
                $"Bridge protocol '{protocol}' is not registered.");
    }
}

/// <summary>
/// Stateless Bridge Node dispatcher facade. Host transports can feed decoded
/// <see cref="ActionFrame"/> values here and write the returned <see cref="CapsFrame"/>.
/// </summary>
public sealed class BridgeNode
{
    private readonly BridgeDispatcherRegistry _dispatchers;

    /// <summary>Create a Bridge Node facade over a dispatcher registry.</summary>
    public BridgeNode(BridgeDispatcherRegistry dispatchers)
    {
        _dispatchers = dispatchers ?? throw new ArgumentNullException(nameof(dispatchers));
    }

    /// <summary>Parse <c>bridge_target</c>, resolve a protocol dispatcher, and invoke it.</summary>
    public Task<CapsFrame> DispatchAsync(ActionFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var target = BridgeTargetParser.FromActionFrame(frame);
        var dispatcher = _dispatchers.Resolve(target.Protocol);
        return dispatcher.DispatchAsync(frame, target, cancellationToken);
    }
}

/// <summary>NWP error codes used by Bridge dispatchers.</summary>
public static class BridgeErrorCodes
{
    /// <summary>
    /// The request targets a protocol/direction pair this Bridge Node never declared in
    /// NDP <c>bridge_protocols</c> / <c>bridge_inbound_protocols</c>. (NPS-CR-0010)
    /// </summary>
    public const string DirectionUnsupported = "NWP-BRIDGE-DIRECTION-UNSUPPORTED";

    /// <summary>The invocation does not contain a valid <c>bridge_target</c>.</summary>
    public const string TargetInvalid = "NWP-BRIDGE-TARGET-INVALID";

    /// <summary>The requested bridge protocol has no registered dispatcher.</summary>
    public const string ProtocolUnsupported = "NWP-BRIDGE-PROTOCOL-UNSUPPORTED";

    /// <summary>The target endpoint is invalid or disallowed.</summary>
    public const string EndpointInvalid = "NWP-BRIDGE-ENDPOINT-INVALID";

    /// <summary>The external call failed or returned an unusable response.</summary>
    public const string UpstreamFailed = "NWP-BRIDGE-UPSTREAM-FAILED";

    /// <summary>An inbound Bridge server request named a tool/action that is not exposed.</summary>
    public const string ServerToolNotFound = "NWP-BRIDGE-SERVER-TOOL-NOT-FOUND";

    /// <summary>An inbound Bridge server was not configured with a local action dispatcher.</summary>
    public const string ServerDispatcherMissing = "NWP-BRIDGE-SERVER-DISPATCHER-MISSING";

    /// <summary>An inbound Bridge server local action dispatch failed unexpectedly.</summary>
    public const string ServerDispatchFailed = "NWP-BRIDGE-SERVER-DISPATCH-FAILED";
}
