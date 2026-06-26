// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>
/// Built-in Bridge dispatcher for HTTP and HTTPS endpoints.
/// </summary>
public sealed class HttpBridgeDispatcher : IBridgeDispatcher
{
    /// <summary>Anchor reference used for HTTP bridge response records.</summary>
    public const string ResponseAnchorRef = "nps://bridge/http-response/v1";

    private readonly HttpClient _client;

    /// <summary>Create an HTTP bridge dispatcher over an existing client.</summary>
    public HttpBridgeDispatcher(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public string Protocol => BridgeProtocols.Http;

    /// <inheritdoc />
    public async Task<CapsFrame> DispatchAsync(
        ActionFrame frame,
        BridgeTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(target);

        var uri = BridgeEndpointValidator.ParseHttpEndpoint(target);
        var method = ParseMethod(BridgeTargetParser.GetString(target, "method", "POST"));

        using var request = new HttpRequestMessage(method, uri);
        ApplyHeaders(request, target);
        ApplyBody(request, frame, target, method);

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
            throw new BridgeDispatchException(BridgeErrorCodes.UpstreamFailed, "HTTP bridge request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BridgeDispatchException(BridgeErrorCodes.UpstreamFailed, "HTTP bridge request failed.", ex);
        }

        using (response)
        {
            var bodyText = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            var record = BuildResponseRecord(response, bodyText);
            return new CapsFrame
            {
                AnchorRef = ResponseAnchorRef,
                Count = 1,
                Data = new[] { record },
                TokenEst = EstimateTokenCost(bodyText)
            };
        }
    }

    private static HttpMethod ParseMethod(string? method)
    {
        var normalized = string.IsNullOrWhiteSpace(method) ? "POST" : method.Trim().ToUpperInvariant();
        return new HttpMethod(normalized);
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
            if (string.IsNullOrEmpty(value))
                continue;

            if (!request.Headers.TryAddWithoutValidation(header.Name, value))
            {
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }
    }

    private static void ApplyBody(
        HttpRequestMessage request,
        ActionFrame frame,
        BridgeTarget target,
        HttpMethod method)
    {
        if (method == HttpMethod.Get || method == HttpMethod.Head)
            return;

        JsonElement body;
        if (frame.Params is { } parameters &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("body", out var frameBody))
        {
            body = frameBody;
        }
        else if (BridgeTargetParser.TryGetJson(target, "body", out var targetBody))
        {
            body = targetBody;
        }
        else
        {
            return;
        }

        var mediaType = BridgeTargetParser.GetString(target, "content_type", "application/json")!;
        request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, mediaType);
    }

    private static JsonElement BuildResponseRecord(HttpResponseMessage response, string bodyText)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("status_code", (int)response.StatusCode);
            writer.WriteString("reason_phrase", response.ReasonPhrase);
            writer.WriteBoolean("success", response.IsSuccessStatusCode);
            writer.WriteString("content_type", response.Content.Headers.ContentType?.ToString());

            writer.WritePropertyName("headers");
            writer.WriteStartObject();
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                writer.WriteString(header.Key, string.Join(",", header.Value));
            writer.WriteEndObject();

            WriteBody(writer, bodyText, response.Content.Headers.ContentType);
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static void WriteBody(Utf8JsonWriter writer, string bodyText, MediaTypeHeaderValue? contentType)
    {
        if (!string.IsNullOrWhiteSpace(bodyText) &&
            contentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var body = JsonDocument.Parse(bodyText);
                writer.WritePropertyName("body");
                body.RootElement.WriteTo(writer);
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
