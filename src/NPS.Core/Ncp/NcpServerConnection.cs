// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.IO;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
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
    private readonly Stream        _stream;
    private readonly NpsFrameCodec _codec;

    /// <summary>The <see cref="HelloFrame"/> sent by the connecting client.</summary>
    public HelloFrame ClientHello { get; }

    internal NcpServerConnection(
        TcpClient     tcp,
        Stream        stream,
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
    /// The encoding policy is negotiated from the client's <c>SupportedEncodings</c> list.
    /// </summary>
    public async Task<NcpSession> AcceptAsync(
        NcpHandshakeCapsFrame serverCaps,
        CancellationToken     ct = default)
    {
        var policy = NegotiateEncodingPolicy(ClientHello);
        var caps = serverCaps with
        {
            NegotiatedEncoding = NcpEncodingPolicy.EncodingToken(policy.DefaultTier),
            EnabledEncodings   = policy.EnabledEncodings,
        };
        var wire = _codec.Encode(caps, policy.DefaultTier);
        await _stream.WriteAsync(wire, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        return new NcpSession(_tcp, _stream, caps, policy);
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
    /// Selects a stable default encoding from the client's <c>SupportedEncodings</c> list.
    /// Optional encodings such as BinaryVector are recorded as extensions, not defaults.
    /// </summary>
    private static NcpEncodingPolicy NegotiateEncodingPolicy(HelloFrame hello)
    {
        var binaryVectorEnabled = hello.SupportedEncodings.Contains("binary_vector.v1");

        foreach (var enc in hello.SupportedEncodings)
        {
            if (enc is "msgpack")
                return new NcpEncodingPolicy(EncodingTier.MsgPack, binaryVectorEnabled);
            if (enc is "json")
                return new NcpEncodingPolicy(EncodingTier.Json, binaryVectorEnabled);
        }

        throw new NpsEncodingUnsupportedException(
            "Client did not offer a supported stable default encoding (expected msgpack or json).");
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
    }
}
