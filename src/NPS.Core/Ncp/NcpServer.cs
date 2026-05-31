// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Ncp;

/// <summary>
/// NCP native-mode TCP server. Listens on a configured endpoint, validates the
/// connection preamble, reads the client's <see cref="HelloFrame"/>, and returns
/// an <see cref="NcpServerConnection"/> for the application to accept or reject (NPS-1 §4.6).
/// </summary>
public sealed class NcpServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly TcpListener   _listener;
    private readonly NpsFrameCodec _codec;

    /// <param name="endpoint">Local endpoint to listen on.</param>
    /// <param name="codec">Codec instance shared with accepted connections.</param>
    public NcpServer(IPEndPoint endpoint, NpsFrameCodec codec)
    {
        _listener = new TcpListener(endpoint);
        _codec    = codec;
    }

    /// <param name="port">Port to listen on (all interfaces).</param>
    /// <param name="codec">Codec instance shared with accepted connections.</param>
    public NcpServer(int port, NpsFrameCodec codec)
        : this(new IPEndPoint(IPAddress.Any, port), codec) { }

    /// <summary>Starts the listener so <see cref="AcceptConnectionAsync"/> can be called.</summary>
    public void Start() => _listener.Start();

    /// <summary>Stops the listener and releases the port binding.</summary>
    public void Stop() => _listener.Stop();

    /// <summary>
    /// Accepts the next inbound connection, validates the NPS preamble, reads the
    /// client's <see cref="HelloFrame"/>, and returns an <see cref="NcpServerConnection"/>.
    /// </summary>
    /// <exception cref="NcpPreambleInvalidException">Client did not send the valid preamble.</exception>
    /// <exception cref="NpsFrameException">First frame was not a <see cref="HelloFrame"/>.</exception>
    public async Task<NcpServerConnection> AcceptConnectionAsync(CancellationToken ct = default)
    {
        TcpClient     tcp    = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        NetworkStream stream = tcp.GetStream();

        try
        {
            // 1 — read & validate preamble (with per-spec 10-second timeout)
            stream.ReadTimeout = (int)NcpPreamble.ReadTimeout.TotalMilliseconds;
            var preambleBuf = new byte[NcpPreamble.Length];
            await stream.ReadExactlyAsync(preambleBuf, ct).ConfigureAwait(false);
            stream.ReadTimeout = Timeout.Infinite;      // restore for subsequent reads

            NcpPreamble.Validate(preambleBuf);          // throws NcpPreambleInvalidException on mismatch

            // 2 — read frame header
            var (header, _) = await NcpNativeClient.ReadFrameHeaderAsync(stream, ct).ConfigureAwait(false);

            if (header.FrameType != FrameType.Hello)
                throw new NpsFrameException(
                    $"Expected HelloFrame (0x{(byte)FrameType.Hello:X2}) as first frame after preamble, " +
                    $"got 0x{(byte)header.FrameType:X2}.");

            // 3 — read payload and deserialise HelloFrame
            var payload = new byte[header.PayloadLength];
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

            var hello = JsonSerializer.Deserialize<HelloFrame>(payload, JsonOpts)
                        ?? throw new NpsFrameException("HelloFrame payload deserialised to null.");

            return new NcpServerConnection(tcp, stream, _codec, hello);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            tcp.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
