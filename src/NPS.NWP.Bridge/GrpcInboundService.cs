// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using LabAcacia.GrpcIngress.Generated;
using NPS.Core;

namespace NPS.NWP.Bridge;

/// <summary>
/// Inbound gRPC server surface of a Bridge Node (NWP §2.1 inbound profile, §16.1.2).
///
/// <para>
/// Serves the <c>NwpIngress</c> protobuf contract that <c>LabAcacia.GrpcIngress</c> published —
/// its clients hold generated stubs, so the <c>.proto</c> is public API and is carried over
/// unchanged. What did change is the error mapping: the ingress collapsed 401 and 403 both onto
/// <c>PERMISSION_DENIED</c> and every 5xx onto <c>UNAVAILABLE</c>. NWP §16.3 forbids collapsing
/// distinct NPS status classes, so this implementation maps through <see cref="BridgeErrorMap"/> —
/// the same table the MCP and A2A surfaces use, in both directions.
/// </para>
/// </summary>
public sealed class GrpcInboundService : NwpIngress.NwpIngressBase
{
    private readonly BridgeInboundOptions _options;
    private readonly IReadOnlyList<INwpBackend> _backends;

    /// <summary>Create the inbound gRPC service over the given backends.</summary>
    public GrpcInboundService(BridgeInboundOptions options, IReadOnlyList<INwpBackend> backends)
    {
        _options = options;
        _backends = backends;
    }

    /// <inheritdoc />
    public override async Task<ManifestResponse> GetManifest(ManifestRequest request, ServerCallContext context)
    {
        var backend = await ResolveAsync(request.Ctx, context).ConfigureAwait(false);

        var descriptor = await backend.GetDescriptorAsync(context.CancellationToken).ConfigureAwait(false);
        var manifest = await backend.GetManifestAsync(context.CancellationToken).ConfigureAwait(false);
        Ensure(manifest);

        return new ManifestResponse
        {
            NwmJson = ByteString.CopyFromUtf8(manifest.Payload?.GetRawText() ?? "{}"),
            NodeType = descriptor.Role == NwpNodeRole.Unknown
                ? string.Empty
                : descriptor.Role.ToString().ToLowerInvariant(),
        };
    }

    /// <inheritdoc />
    public override async Task<ActionsResponse> ListActions(ActionsRequest request, ServerCallContext context)
    {
        var backend = await ResolveAsync(request.Ctx, context).ConfigureAwait(false);
        var actions = await backend.GetActionsAsync(context.CancellationToken).ConfigureAwait(false);

        var body = JsonSerializer.SerializeToElement(new
        {
            actions = actions.ToDictionary(
                a => a.ActionId,
                a => new { description = a.Description }),
        }, BridgeFrameJson.Json);

        return new ActionsResponse { ActionsJson = ByteString.CopyFromUtf8(body.GetRawText()) };
    }

    /// <inheritdoc />
    public override async Task<InvokeResponse> Invoke(InvokeRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.ActionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "action_id is required"));

        var backend = await ResolveAsync(request.Ctx, context).ConfigureAwait(false);

        JsonElement? args = request.ParamsJson.IsEmpty
            ? null
            : JsonSerializer.Deserialize<JsonElement>(request.ParamsJson.ToStringUtf8());

        var result = await backend
            .InvokeAsync(request.ActionId, args, async: false, context.CancellationToken)
            .ConfigureAwait(false);

        Ensure(result);

        var payload = result.Payload?.GetRawText() ?? "{}";
        return new InvokeResponse
        {
            HttpStatus = 200,
            BodyJson = ByteString.CopyFromUtf8(payload),
            TaskId = TryReadString(result.Payload, "task_id") ?? string.Empty,
        };
    }

    /// <inheritdoc />
    public override async Task<QueryResponse> Query(QueryRequest request, ServerCallContext context)
    {
        var backend = await ResolveAsync(request.Ctx, context).ConfigureAwait(false);

        var query = request.QueryJson.IsEmpty
            ? JsonSerializer.SerializeToElement(new { }, BridgeFrameJson.Json)
            : JsonSerializer.Deserialize<JsonElement>(request.QueryJson.ToStringUtf8());

        var result = await backend.QueryAsync(query, context.CancellationToken).ConfigureAwait(false);
        Ensure(result);

        return new QueryResponse
        {
            HttpStatus = 200,
            BodyJson = ByteString.CopyFromUtf8(result.Payload?.GetRawText() ?? "{}"),
        };
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Turn a failed NWP result into an <see cref="RpcException"/> carrying the §16.3 gRPC status.
    /// The NPS status and NWP error code travel in the detail, so a caller can recover the exact
    /// NPS fault rather than only the coarse gRPC class.
    /// </summary>
    private static void Ensure(NwpResult result)
    {
        if (result.Ok) return;

        var code = Enum.TryParse<StatusCode>(
            BridgeErrorMap.ToGrpcStatus(result.NpsStatus).Replace("_", string.Empty),
            ignoreCase: true,
            out var parsed)
            ? parsed
            : StatusCode.Internal;

        var detail = new StringBuilder(result.NpsStatus ?? NpsStatusCodes.ServerInternal);
        if (!string.IsNullOrEmpty(result.NwpError)) detail.Append(' ').Append(result.NwpError);
        if (!string.IsNullOrEmpty(result.Message)) detail.Append(": ").Append(result.Message);

        throw new RpcException(new Status(code, detail.ToString()));
    }

    private async Task<INwpBackend> ResolveAsync(UpstreamContext? ctx, ServerCallContext context)
    {
        if (!_options.ServesInbound("grpc"))
        {
            throw new RpcException(new Status(
                StatusCode.Unimplemented,
                $"{NpsStatusCodes.ServerUnsupported} {BridgeErrorCodes.DirectionUnsupported}: " +
                "this Bridge Node does not declare \"grpc\" in bridge_inbound_protocols."));
        }

        var name = ctx?.Upstream;

        foreach (var backend in _backends)
        {
            var descriptor = await backend.GetDescriptorAsync(context.CancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(name) && _backends.Count == 1) return backend;
            if (string.Equals(descriptor.Name, name, StringComparison.OrdinalIgnoreCase)) return backend;
        }

        throw new RpcException(new Status(
            StatusCode.NotFound,
            $"{NpsStatusCodes.ClientNotFound} {BridgeErrorCodes.ServerToolNotFound}: " +
            $"no NWP node named '{name}' is fronted by this Bridge Node."));
    }

    private static string? TryReadString(JsonElement? payload, string name) =>
        payload.HasValue &&
        payload.Value.ValueKind == JsonValueKind.Object &&
        payload.Value.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
