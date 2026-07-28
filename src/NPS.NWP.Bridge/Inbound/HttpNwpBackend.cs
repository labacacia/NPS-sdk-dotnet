// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using NPS.Core;

namespace NPS.NWP.Bridge;

/// <summary>Declares one remote NWP node that an inbound Bridge server fronts over HTTP.</summary>
public sealed record NwpUpstream
{
    /// <summary>Logical name. Namespaces MCP resource URIs and tool names, so it MUST be unique per Bridge.</summary>
    public required string Name { get; init; }

    /// <summary>Base URL of the NWP node (scheme + host + path prefix, no trailing slash).</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>Optional Agent NID forwarded as <c>X-NWP-Agent</c>.</summary>
    public string? AgentNid { get; init; }

    /// <summary>Optional bearer token or similar, forwarded as <c>Authorization</c>.</summary>
    public string? AuthHeader { get; init; }

    /// <summary>Max rows a single query may pull from a Memory Node. NWP's own page cap is 1000.</summary>
    public uint ReadLimit { get; init; } = 100;
}

/// <summary>
/// An <see cref="INwpBackend"/> that fronts a <b>remote</b> NWP node over HTTP — the
/// standalone-gateway shape that <c>compat/{mcp,grpc,a2a}-ingress</c> shipped before
/// NPS-CR-0010 absorbed them. Reads <c>/.nwm</c>, <c>/actions</c>, <c>/query</c>, <c>/invoke</c>.
/// </summary>
public sealed class HttpNwpBackend : INwpBackend
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly NwpUpstream _upstream;
    private NwpNodeDescriptor? _cached;

    /// <summary>Create a backend for one remote NWP node.</summary>
    public HttpNwpBackend(HttpClient http, NwpUpstream upstream)
    {
        _http = http;
        _upstream = upstream;
    }

    /// <summary>The upstream this backend fronts.</summary>
    public NwpUpstream Upstream => _upstream;

    /// <inheritdoc />
    public async Task<NwpNodeDescriptor> GetDescriptorAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null) return _cached;

        try
        {
            using var response = await SendAsync(HttpMethod.Get, "/.nwm", null, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return _cached = Unknown();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var nwm = JsonSerializer.Deserialize<NwmSnapshot>(body, Json);

            return _cached = new NwpNodeDescriptor
            {
                Name = _upstream.Name,
                Role = NwpNodeDescriptor.ParseRole(nwm?.NodeType),
                DisplayName = nwm?.DisplayName,
                Description = nwm?.Description,
            };
        }
        catch
        {
            // An unreachable NWM must not take the whole Bridge down: the node is simply
            // projected onto nothing until it comes back.
            return _cached = Unknown();
        }
    }

    /// <inheritdoc />
    public Task<NwpResult> GetManifestAsync(CancellationToken cancellationToken = default) =>
        SendForResultAsync(HttpMethod.Get, "/.nwm", null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<NwpActionDescriptor>> GetActionsAsync(CancellationToken cancellationToken = default)
    {
        var descriptor = await GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        if (!descriptor.IsInvokable) return Array.Empty<NwpActionDescriptor>();

        using var response = await SendAsync(HttpMethod.Get, "/actions", null, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return Array.Empty<NwpActionDescriptor>();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("actions", out var actions) ||
            actions.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<NwpActionDescriptor>();
        }

        var list = new List<NwpActionDescriptor>();
        foreach (var action in actions.EnumerateObject())
        {
            list.Add(new NwpActionDescriptor
            {
                ActionId = action.Name,
                Description = action.Value.TryGetProperty("description", out var d) &&
                              d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null,
                InputSchema = action.Value.TryGetProperty("params_schema", out var s)
                    ? s.Clone()
                    : null,
            });
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<NwpResult> QueryAsync(JsonElement query, CancellationToken cancellationToken = default)
    {
        var descriptor = await GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        if (!descriptor.IsQueryable)
        {
            return NwpResult.Failure(
                NpsStatusCodes.ServerUnsupported,
                BridgeErrorCodes.ServerToolNotFound,
                $"Upstream '{_upstream.Name}' is not queryable (role: {descriptor.Role}).");
        }

        return await SendForResultAsync(HttpMethod.Post, "/query", query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NwpResult> InvokeAsync(
        string actionId,
        JsonElement? arguments,
        bool async,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToElement(new
        {
            action_id = actionId,
            @params = arguments,
            async,
        }, Json);

        return await SendForResultAsync(HttpMethod.Post, "/invoke", body, cancellationToken).ConfigureAwait(false);
    }

    // ── HTTP plumbing ────────────────────────────────────────────────────────

    private async Task<NwpResult> SendForResultAsync(
        HttpMethod method,
        string subPath,
        JsonElement? body,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(method, subPath, body, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NwpResult.Failure(
                NpsStatusCodes.ServerTimeout,
                BridgeErrorCodes.UpstreamFailed,
                $"Upstream '{_upstream.Name}' timed out.");
        }
        catch (HttpRequestException ex)
        {
            return NwpResult.Failure(
                NpsStatusCodes.DownstreamUnavailable,
                BridgeErrorCodes.UpstreamFailed,
                $"Upstream '{_upstream.Name}' is unreachable: {ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Translate the upstream's status into an NPS status (NWP §16.3) rather than
                // forwarding an opaque body — that is what lets the inbound server emit a real
                // protocol-level error instead of a "successful" response carrying error text.
                return NwpResult.Failure(
                    BridgeErrorMap.FromHttpStatus((int)response.StatusCode),
                    TryReadNwpError(text),
                    text);
            }

            try
            {
                return NwpResult.Success(JsonSerializer.Deserialize<JsonElement>(text));
            }
            catch (JsonException ex)
            {
                return NwpResult.Failure(
                    NpsStatusCodes.DownstreamUnavailable,
                    BridgeErrorCodes.UpstreamFailed,
                    $"Upstream '{_upstream.Name}' returned a body that is not valid JSON: {ex.Message}");
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string subPath,
        JsonElement? body,
        CancellationToken cancellationToken)
    {
        var url = new Uri(_upstream.BaseUrl.ToString().TrimEnd('/') + subPath);
        using var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body.Value, options: Json);
            request.Content.Headers.ContentType!.MediaType = "application/nwp-frame";
        }

        if (!string.IsNullOrEmpty(_upstream.AgentNid))
            request.Headers.Add("X-NWP-Agent", _upstream.AgentNid);
        if (!string.IsNullOrEmpty(_upstream.AuthHeader))
            request.Headers.TryAddWithoutValidation("Authorization", _upstream.AuthHeader);

        return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Pull the NWP error code out of an upstream ErrorFrame body, if it carries one.</summary>
    private static string? TryReadNwpError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private NwpNodeDescriptor Unknown() => new()
    {
        Name = _upstream.Name,
        Role = NwpNodeRole.Unknown,
    };

    /// <summary>Minimal NWM projection — only what is needed to pick the node's foreign-protocol surface.</summary>
    private sealed record NwmSnapshot
    {
        public string? NodeType { get; init; }
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
    }
}
