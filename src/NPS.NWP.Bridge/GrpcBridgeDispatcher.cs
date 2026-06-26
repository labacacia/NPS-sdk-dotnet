// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>
/// Built-in Bridge dispatcher for unary gRPC calls using the JSON gRPC codec
/// (<c>application/grpc+json</c>). The endpoint path identifies the service and
/// method, for example <c>https://host/Package.Service/Method</c>.
/// </summary>
public sealed class GrpcBridgeDispatcher : IBridgeDispatcher
{
    /// <summary>Anchor reference used for gRPC bridge response records.</summary>
    public const string ResponseAnchorRef = "nps://bridge/grpc-json-response/v1";

    private readonly HttpClient _client;

    /// <summary>Create a gRPC bridge dispatcher over an existing client.</summary>
    public GrpcBridgeDispatcher(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public string Protocol => BridgeProtocols.Grpc;

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
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Content = new ByteArrayContent(BuildGrpcMessage(frame, target)),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc+json");
        request.Headers.TryAddWithoutValidation("te", "trailers");
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
            throw new BridgeDispatchException(BridgeErrorCodes.UpstreamFailed, "gRPC bridge request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BridgeDispatchException(BridgeErrorCodes.UpstreamFailed, "gRPC bridge request failed.", ex);
        }

        using (response)
        {
            var bytes = response.Content is null
                ? Array.Empty<byte>()
                : await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);

            var record = BuildResponseRecord(response, bytes);
            return new CapsFrame
            {
                AnchorRef = ResponseAnchorRef,
                Count = 1,
                Data = new[] { record },
                TokenEst = EstimateTokenCost(bytes.Length)
            };
        }
    }

    private static byte[] BuildGrpcMessage(ActionFrame frame, BridgeTarget target)
    {
        JsonElement payload;
        if (BridgeTargetParser.TryGetJson(target, "grpc_message", out var targetMessage) ||
            BridgeTargetParser.TryGetJson(target, "message", out targetMessage) ||
            BridgeTargetParser.TryGetJson(target, "body", out targetMessage))
        {
            payload = targetMessage;
        }
        else if (frame.Params is { } parameters &&
                 parameters.ValueKind == JsonValueKind.Object &&
                 parameters.TryGetProperty("grpc_message", out var frameMessage))
        {
            payload = frameMessage;
        }
        else if (frame.Params is { } frameParams)
        {
            payload = frameParams;
        }
        else
        {
            payload = JsonSerializer.SerializeToElement(new { });
        }

        var json = Encoding.UTF8.GetBytes(payload.GetRawText());
        var wire = new byte[json.Length + 5];
        wire[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(1, 4), (uint)json.Length);
        json.CopyTo(wire.AsSpan(5));
        return wire;
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

    private static JsonElement BuildResponseRecord(HttpResponseMessage response, byte[] body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("status_code", (int)response.StatusCode);
            writer.WriteBoolean("success", response.IsSuccessStatusCode && ReadGrpcStatus(response) is "0" or null);
            writer.WriteString("content_type", response.Content.Headers.ContentType?.ToString());
            writer.WriteString("grpc_status", ReadGrpcStatus(response));
            writer.WriteString("grpc_message", ReadGrpcMessage(response));

            writer.WritePropertyName("headers");
            writer.WriteStartObject();
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                writer.WriteString(header.Key, string.Join(",", header.Value));
            writer.WriteEndObject();

            writer.WritePropertyName("trailers");
            writer.WriteStartObject();
            foreach (var header in response.TrailingHeaders)
                writer.WriteString(header.Key, string.Join(",", header.Value));
            writer.WriteEndObject();

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in ReadGrpcMessages(body))
                WriteMessage(writer, message);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static string? ReadGrpcStatus(HttpResponseMessage response) =>
        ReadHeader(response, "grpc-status");

    private static string? ReadGrpcMessage(HttpResponseMessage response) =>
        ReadHeader(response, "grpc-message");

    private static string? ReadHeader(HttpResponseMessage response, string name)
    {
        if (response.TrailingHeaders.TryGetValues(name, out var trailers))
            return trailers.FirstOrDefault();
        if (response.Headers.TryGetValues(name, out var headers))
            return headers.FirstOrDefault();
        return null;
    }

    private static IEnumerable<byte[]> ReadGrpcMessages(byte[] body)
    {
        var offset = 0;
        while (body.Length - offset >= 5)
        {
            var compressed = body[offset] != 0;
            var length = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(offset + 1, 4));
            offset += 5;

            if (compressed || length > int.MaxValue || body.Length - offset < length)
                yield break;

            var message = new byte[length];
            body.AsSpan(offset, (int)length).CopyTo(message);
            offset += (int)length;
            yield return message;
        }
    }

    private static void WriteMessage(Utf8JsonWriter writer, byte[] message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            doc.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteBase64StringValue(message);
        }
    }

    private static uint EstimateTokenCost(int byteLength) =>
        byteLength == 0 ? 0 : (uint)Math.Max(1, byteLength / 4);
}
