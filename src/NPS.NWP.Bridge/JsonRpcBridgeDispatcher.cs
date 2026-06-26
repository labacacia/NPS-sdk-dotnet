// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>
/// Base dispatcher for JSON-RPC 2.0 protocols transported over HTTP POST.
/// </summary>
public abstract class JsonRpcBridgeDispatcher : IBridgeDispatcher
{
    private readonly HttpClient _client;
    private readonly string _defaultMethod;
    private readonly string _responseAnchorRef;

    /// <summary>Create a JSON-RPC bridge dispatcher.</summary>
    protected JsonRpcBridgeDispatcher(HttpClient client, string defaultMethod, string responseAnchorRef)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _defaultMethod = string.IsNullOrWhiteSpace(defaultMethod)
            ? throw new ArgumentException("Default JSON-RPC method must not be empty.", nameof(defaultMethod))
            : defaultMethod;
        _responseAnchorRef = string.IsNullOrWhiteSpace(responseAnchorRef)
            ? throw new ArgumentException("Response anchor reference must not be empty.", nameof(responseAnchorRef))
            : responseAnchorRef;
    }

    /// <inheritdoc />
    public abstract string Protocol { get; }

    /// <inheritdoc />
    public async Task<CapsFrame> DispatchAsync(
        ActionFrame frame,
        BridgeTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(target);

        var uri = BridgeEndpointValidator.ParseHttpEndpoint(target);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(BuildRequestBody(frame, target), Encoding.UTF8, "application/json")
        };
        ApplyHeaders(request, target);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (frame.TimeoutMs > 0)
            timeout.CancelAfter(TimeSpan.FromMilliseconds(frame.TimeoutMs));

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BridgeDispatchException(BridgeErrorCodes.UpstreamFailed, $"{Protocol} JSON-RPC bridge request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BridgeDispatchException(BridgeErrorCodes.UpstreamFailed, $"{Protocol} JSON-RPC bridge request failed.", ex);
        }

        using (response)
        {
            var bodyText = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            var record = BuildResponseRecord(response, bodyText);
            return new CapsFrame
            {
                AnchorRef = _responseAnchorRef,
                Count = 1,
                Data = new[] { record },
                TokenEst = EstimateTokenCost(bodyText)
            };
        }
    }

    private string BuildRequestBody(ActionFrame frame, BridgeTarget target)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            WriteRequestId(writer, frame, target);
            writer.WriteString("method", ReadRpcMethod(frame, target));
            writer.WritePropertyName("params");
            WriteRpcParams(writer, frame, target);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private string ReadRpcMethod(ActionFrame frame, BridgeTarget target)
    {
        var method = BridgeTargetParser.GetString(target, "rpc_method")
            ?? BridgeTargetParser.GetString(target, "method");

        if (!string.IsNullOrWhiteSpace(method))
            return method!;

        if (frame.Params is { } parameters &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("rpc_method", out var frameMethod) &&
            frameMethod.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(frameMethod.GetString()))
        {
            return frameMethod.GetString()!;
        }

        return _defaultMethod;
    }

    private static void WriteRequestId(Utf8JsonWriter writer, ActionFrame frame, BridgeTarget target)
    {
        if (BridgeTargetParser.TryGetJson(target, "id", out var targetId))
        {
            targetId.WriteTo(writer);
            return;
        }

        if (frame.Params is { } parameters &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("id", out var frameId))
        {
            frameId.WriteTo(writer);
            return;
        }

        writer.WriteStringValue(frame.RequestId ?? frame.IdempotencyKey ?? Guid.NewGuid().ToString("N"));
    }

    private static void WriteRpcParams(Utf8JsonWriter writer, ActionFrame frame, BridgeTarget target)
    {
        if (BridgeTargetParser.TryGetJson(target, "rpc_params", out var targetRpcParams) ||
            BridgeTargetParser.TryGetJson(target, "params", out targetRpcParams))
        {
            targetRpcParams.WriteTo(writer);
            return;
        }

        if (frame.Params is not { } parameters || parameters.ValueKind != JsonValueKind.Object)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        foreach (var name in new[] { "rpc_params", "params", "body" })
        {
            if (parameters.TryGetProperty(name, out var selected))
            {
                selected.WriteTo(writer);
                return;
            }
        }

        writer.WriteStartObject();
        foreach (var property in parameters.EnumerateObject())
        {
            if (property.NameEquals("bridge_target") ||
                property.NameEquals("rpc_method") ||
                property.NameEquals("method") ||
                property.NameEquals("id"))
            {
                continue;
            }

            property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void ApplyHeaders(HttpRequestMessage request, BridgeTarget target)
    {
        if (!BridgeTargetParser.TryGetJson(target, "headers", out var headers) ||
            headers.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var header in headers.EnumerateObject())
        {
            if (header.Value.ValueKind != JsonValueKind.String)
                continue;

            var value = header.Value.GetString();
            if (!string.IsNullOrEmpty(value))
                request.Headers.TryAddWithoutValidation(header.Name, value);
        }
    }

    private static JsonElement BuildResponseRecord(HttpResponseMessage response, string bodyText)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("status_code", (int)response.StatusCode);
            writer.WriteBoolean("success", response.IsSuccessStatusCode);
            writer.WriteString("content_type", response.Content.Headers.ContentType?.ToString());

            writer.WritePropertyName("headers");
            writer.WriteStartObject();
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                writer.WriteString(header.Key, string.Join(",", header.Value));
            writer.WriteEndObject();

            WriteJsonRpcBody(writer, bodyText, response.Content.Headers.ContentType);
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static void WriteJsonRpcBody(Utf8JsonWriter writer, string bodyText, MediaTypeHeaderValue? contentType)
    {
        if (!string.IsNullOrWhiteSpace(bodyText) &&
            contentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var body = JsonDocument.Parse(bodyText);
                writer.WritePropertyName("jsonrpc_response");
                body.RootElement.WriteTo(writer);

                if (body.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (body.RootElement.TryGetProperty("result", out var result))
                    {
                        writer.WritePropertyName("result");
                        result.WriteTo(writer);
                    }

                    if (body.RootElement.TryGetProperty("error", out var error))
                    {
                        writer.WritePropertyName("error");
                        error.WriteTo(writer);
                    }
                }

                return;
            }
            catch (JsonException)
            {
                // Fall through to body_text for mislabeled upstream payloads.
            }
        }

        writer.WriteString("body_text", bodyText);
    }

    private static uint EstimateTokenCost(string bodyText)
    {
        if (string.IsNullOrEmpty(bodyText))
            return 0;

        return (uint)Math.Max(1, bodyText.Length / 4);
    }
}
