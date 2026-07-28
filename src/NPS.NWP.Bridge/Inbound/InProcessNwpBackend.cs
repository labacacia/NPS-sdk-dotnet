// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>Dispatches an <see cref="ActionFrame"/> to a co-hosted NPS node.</summary>
public delegate Task<IFrame> NwpInProcessActionDispatcher(
    ActionFrame frame,
    CancellationToken cancellationToken = default);

/// <summary>
/// Dispatches a <see cref="QueryFrame"/> to a co-hosted NPS Memory / Complex Node.
/// Optional: a deployment that fronts only Action Nodes leaves this unset, and its
/// inbound Bridge then serves <c>resources/*</c> over an empty resource set — still
/// conformant, since NWP §16.1.2 requires the <i>methods</i> to be served, not that a
/// Memory Node exist behind them.
/// </summary>
public delegate Task<IFrame> NwpInProcessQueryDispatcher(
    QueryFrame frame,
    CancellationToken cancellationToken = default);

/// <summary>
/// An <see cref="INwpBackend"/> that dispatches in-process, without any network hop —
/// the shape where an NPS node additionally serves a foreign protocol itself. This is
/// the deployment the .NET SDK's pre-CR-0010 <c>BridgeServerOptions</c> supported, and
/// it is preserved here verbatim in capability while gaining a queryable surface.
/// </summary>
public sealed class InProcessNwpBackend : INwpBackend
{
    private readonly NwpNodeDescriptor _descriptor;
    private readonly IReadOnlyList<NwpActionDescriptor> _actions;
    private readonly NwpInProcessActionDispatcher? _invoke;
    private readonly NwpInProcessQueryDispatcher? _query;

    /// <summary>Create an in-process backend fronting a co-hosted node.</summary>
    /// <param name="descriptor">Identity and role of the co-hosted node.</param>
    /// <param name="actions">Actions to project onto the foreign protocol's call surface.</param>
    /// <param name="invoke">Action dispatcher. Required when the node is invokable.</param>
    /// <param name="query">Query dispatcher. Required when the node is queryable.</param>
    public InProcessNwpBackend(
        NwpNodeDescriptor descriptor,
        IReadOnlyList<NwpActionDescriptor>? actions = null,
        NwpInProcessActionDispatcher? invoke = null,
        NwpInProcessQueryDispatcher? query = null)
    {
        _descriptor = descriptor;
        _actions = actions ?? Array.Empty<NwpActionDescriptor>();
        _invoke = invoke;
        _query = query;
    }

    /// <inheritdoc />
    public Task<NwpNodeDescriptor> GetDescriptorAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_descriptor);

    /// <inheritdoc />
    public Task<NwpResult> GetManifestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(NwpResult.Success(JsonSerializer.SerializeToElement(new
        {
            node_type = _descriptor.Role.ToString().ToLowerInvariant(),
            display_name = _descriptor.DisplayName ?? _descriptor.Name,
            description = _descriptor.Description,
        }, BridgeFrameJson.Json)));

    /// <inheritdoc />
    public Task<IReadOnlyList<NwpActionDescriptor>> GetActionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_descriptor.IsInvokable ? _actions : Array.Empty<NwpActionDescriptor>());

    /// <inheritdoc />
    public async Task<NwpResult> QueryAsync(JsonElement query, CancellationToken cancellationToken = default)
    {
        if (!_descriptor.IsQueryable)
        {
            return NwpResult.Failure(
                NpsStatusCodes.ServerUnsupported,
                BridgeErrorCodes.ServerToolNotFound,
                $"Node '{_descriptor.Name}' is not queryable (role: {_descriptor.Role}).");
        }

        if (_query is null)
        {
            return NwpResult.Failure(
                NpsStatusCodes.ServerInternal,
                BridgeErrorCodes.ServerDispatcherMissing,
                $"Node '{_descriptor.Name}' declares a queryable role but no query dispatcher was configured.");
        }

        var frame = new QueryFrame { Filter = query.Clone() };

        try
        {
            var response = await _query(frame, cancellationToken).ConfigureAwait(false);
            return ToResult(response);
        }
        catch (Exception ex)
        {
            return NwpResult.DispatchFailed(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<NwpResult> InvokeAsync(
        string actionId,
        JsonElement? arguments,
        bool async,
        CancellationToken cancellationToken = default)
    {
        if (_invoke is null)
        {
            return NwpResult.Failure(
                NpsStatusCodes.ServerInternal,
                BridgeErrorCodes.ServerDispatcherMissing,
                $"Node '{_descriptor.Name}' has no action dispatcher configured.");
        }

        var frame = new ActionFrame
        {
            ActionId = actionId,
            Params = arguments?.Clone(),
            Async = async,
        };

        try
        {
            var response = await _invoke(frame, cancellationToken).ConfigureAwait(false);
            return ToResult(response);
        }
        catch (Exception ex)
        {
            return NwpResult.DispatchFailed(ex.Message);
        }
    }

    /// <summary>
    /// Project a co-hosted node's frame response onto <see cref="NwpResult"/>, preserving the
    /// NPS status so the inbound server can map it per NWP §16.3 rather than forwarding an
    /// opaque body.
    /// </summary>
    private static NwpResult ToResult(IFrame frame)
    {
        if (frame is ErrorFrame error)
        {
            return NwpResult.Failure(
                string.IsNullOrWhiteSpace(error.Status) ? NpsStatusCodes.ServerInternal : error.Status,
                error.Error,
                error.Message);
        }

        return NwpResult.Success(JsonSerializer.Deserialize<JsonElement>(BridgeFrameJson.Serialize(frame)));
    }
}
