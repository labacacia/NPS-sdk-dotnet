// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Ncp;

/// <summary>
/// Server-side representation of an inbound NCP connection that has passed the preamble
/// check and sent its <see cref="HelloFrame"/>. Call <see cref="AcceptAsync"/> to complete
/// the handshake, or <see cref="RejectAsync"/> to send an error and close the connection.
/// </summary>
public sealed class NcpServerConnection : IAsyncDisposable
{
    private readonly TcpClient     _tcp;
    private readonly NetworkStream _stream;
    private readonly NpsFrameCodec _codec;

    /// <summary>The <see cref="HelloFrame"/> sent by the connecting client.</summary>
    public HelloFrame ClientHello { get; }

    internal NcpServerConnection(
        TcpClient     tcp,
        NetworkStream stream,
        NpsFrameCodec codec,
        HelloFrame    clientHello)
    {
        _tcp        = tcp;
        _stream     = stream;
        _codec      = codec;
        ClientHello = clientHello;
    }

    /// <summary>
    /// Sends <paramref name="serverCaps"/> to the client and returns a live <see cref="NcpSession"/>.
    /// The encoding tier is negotiated from the client's <c>SupportedEncodings</c> list.
    /// </summary>
    public async Task<NcpSession> AcceptAsync(
        NcpHandshakeCapsFrame serverCaps,
        CancellationToken     ct = default)
    {
        var tier = NegotiateEncoding(ClientHello);
        var wire = _codec.Encode(serverCaps, tier);
        await _stream.WriteAsync(wire, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        return new NcpSession(_tcp, _stream, serverCaps, tier);
    }

    /// <summary>
    /// Sends an <see cref="ErrorFrame"/> to reject the client and closes the connection.
    /// </summary>
    public async Task RejectAsync(ErrorFrame error, CancellationToken ct = default)
    {
        try
        {
            var wire = _codec.Encode(error, EncodingTier.Json);
            await _stream.WriteAsync(wire, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Selects the best encoding tier from the client's <c>SupportedEncodings</c> list.
    /// Prefers MsgPack; falls back to JSON.
    /// </summary>
    private static EncodingTier NegotiateEncoding(HelloFrame hello)
    {
        foreach (var enc in hello.SupportedEncodings)
        {
            if (enc is "msgpack") return EncodingTier.MsgPack;
            if (enc is "json")    return EncodingTier.Json;
        }
        return EncodingTier.Json;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
    }
}
