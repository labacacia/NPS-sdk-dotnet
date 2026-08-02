// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core;

namespace NPS.NWP.Bridge;

/// <summary>Role an NWP node carries, as reported by its NWM <c>node_type</c> (NWP §4.1).</summary>
public enum NwpNodeRole
{
    /// <summary>Role could not be determined (NWM unreachable or unrecognized value).</summary>
    Unknown = 0,

    /// <summary>Memory Node — queryable. Projected onto a foreign protocol's read surface.</summary>
    Memory,

    /// <summary>Action Node — invokable. Projected onto a foreign protocol's call surface.</summary>
    Action,

    /// <summary>Complex Node — both queryable and invokable.</summary>
    Complex,

    /// <summary>Anchor Node — cluster control plane. Not projected by inbound Bridges.</summary>
    Anchor,

    /// <summary>Bridge Node — translation. Not projected by inbound Bridges.</summary>
    Bridge,
}

/// <summary>Identity and role of one NWP node fronted by an inbound Bridge server.</summary>
public sealed record NwpNodeDescriptor
{
    /// <summary>Logical name. Namespaces MCP resource URIs and tool names, so it MUST be unique per Bridge.</summary>
    public required string Name { get; init; }

    /// <summary>Node role, driving which foreign-protocol surface this node is projected onto.</summary>
    public required NwpNodeRole Role { get; init; }

    /// <summary>Human-readable name surfaced to foreign clients. Falls back to <see cref="Name"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Short description surfaced to foreign clients.</summary>
    public string? Description { get; init; }

    /// <summary>Whether this node exposes a queryable surface (Memory / Complex).</summary>
    public bool IsQueryable => Role is NwpNodeRole.Memory or NwpNodeRole.Complex;

    /// <summary>Whether this node exposes an invokable surface (Action / Complex).</summary>
    public bool IsInvokable => Role is NwpNodeRole.Action or NwpNodeRole.Complex;

    /// <summary>Parse an NWM <c>node_type</c> string into a role.</summary>
    public static NwpNodeRole ParseRole(string? nodeType) => nodeType?.ToLowerInvariant() switch
    {
        "memory" => NwpNodeRole.Memory,
        "action" => NwpNodeRole.Action,
        "complex" => NwpNodeRole.Complex,
        "anchor" => NwpNodeRole.Anchor,
        "bridge" => NwpNodeRole.Bridge,
        _ => NwpNodeRole.Unknown,
    };
}

/// <summary>One action exposed by an NWP node, as projected onto a foreign protocol's call surface.</summary>
public sealed record NwpActionDescriptor
{
    /// <summary>NPS action identifier dispatched to the node.</summary>
    public required string ActionId { get; init; }

    /// <summary>Short description surfaced to foreign clients.</summary>
    public string? Description { get; init; }

    /// <summary>JSON Schema describing input arguments. Absent ⇒ an open object schema is advertised.</summary>
    public JsonElement? InputSchema { get; init; }

    /// <summary>Whether invocations should request async execution.</summary>
    public bool Async { get; init; }

    /// <summary>Optional tags (A2A skill tags).</summary>
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// Outcome of one NWP operation performed on behalf of a foreign client.
/// Carries either a payload or an NPS status, so that inbound servers can map
/// failures onto their protocol's error space per NWP §16.3 instead of
/// forwarding an opaque body.
/// </summary>
public sealed record NwpResult
{
    /// <summary>Whether the NWP operation succeeded.</summary>
    public required bool Ok { get; init; }

    /// <summary>Response payload when <see cref="Ok"/>.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>NPS status code (spec/status-codes.md) when not <see cref="Ok"/>.</summary>
    public string? NpsStatus { get; init; }

    /// <summary>NWP protocol error code (spec/error-codes.md) when not <see cref="Ok"/>.</summary>
    public string? NwpError { get; init; }

    /// <summary>Human-readable failure detail.</summary>
    public string? Message { get; init; }

    /// <summary>A successful result carrying <paramref name="payload"/>.</summary>
    public static NwpResult Success(JsonElement payload) => new() { Ok = true, Payload = payload };

    /// <summary>A failed result carrying an NPS status and (optionally) an NWP error code.</summary>
    public static NwpResult Failure(string npsStatus, string? nwpError = null, string? message = null) =>
        new() { Ok = false, NpsStatus = npsStatus, NwpError = nwpError, Message = message };

    /// <summary>A failed result for an unexpected dispatch fault.</summary>
    public static NwpResult DispatchFailed(string message) =>
        Failure(NpsStatusCodes.ServerInternal, BridgeErrorCodes.ServerDispatchFailed, message);
}

/// <summary>
/// One NWP node reachable by an inbound Bridge server (NWP §2.1 inbound profile,
/// NPS-CR-0010). Implementations front either a local in-process node
/// (<see cref="InProcessNwpBackend"/>) or a remote node over HTTP
/// (<see cref="HttpNwpBackend"/>). The inbound protocol servers are written
/// against this interface alone and are unaware of which shape they are serving.
/// </summary>
public interface INwpBackend
{
    /// <summary>Identity and role of the fronted node.</summary>
    Task<NwpNodeDescriptor> GetDescriptorAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The node's raw NWM (<c>/.nwm</c>) document. An HTTP-fronted node returns the upstream body
    /// verbatim — inbound gRPC clients read it, so fidelity matters; a co-hosted node synthesises
    /// one from its descriptor.
    /// </summary>
    Task<NwpResult> GetManifestAsync(CancellationToken cancellationToken = default);

    /// <summary>Actions this node exposes. Empty for a node that is not invokable.</summary>
    Task<IReadOnlyList<NwpActionDescriptor>> GetActionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Run an NWP <c>Query</c> against this node. Only meaningful when the node is queryable.</summary>
    Task<NwpResult> QueryAsync(JsonElement query, CancellationToken cancellationToken = default);

    /// <summary>Run an NWP <c>Invoke</c> against this node. Only meaningful when the node is invokable.</summary>
    Task<NwpResult> InvokeAsync(
        string actionId,
        JsonElement? arguments,
        bool async,
        CancellationToken cancellationToken = default);
}
