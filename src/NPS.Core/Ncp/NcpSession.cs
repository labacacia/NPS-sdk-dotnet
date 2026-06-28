// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.IO;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Ncp;

/// <summary>
/// A live NCP native-mode session established after a successful handshake.
/// Wraps the underlying TCP connection and exposes the negotiated parameters.
/// Upper-layer protocols (NWP, NIP, …) obtain the raw stream via <see cref="GetStream"/>.
/// </summary>
public sealed class NcpSession : IAsyncDisposable
{
    private readonly TcpClient     _tcp;
    private readonly Stream        _stream;

    /// <summary>Capabilities the server advertised during the handshake.</summary>
    public NcpHandshakeCapsFrame ServerCaps { get; }

    /// <summary>Encoding policy negotiated during the handshake.</summary>
    public NcpEncodingPolicy EncodingPolicy { get; }

    /// <summary>Stable default encoding tier negotiated during the handshake.</summary>
    public EncodingTier NegotiatedTier => EncodingPolicy.DefaultTier;

    /// <summary><c>true</c> while the underlying TCP connection is still open.</summary>
    public bool IsConnected => _tcp.Connected;

    internal NcpSession(
        TcpClient             tcp,
        Stream                stream,
        NcpHandshakeCapsFrame serverCaps,
        NcpEncodingPolicy     encodingPolicy)
    {
        _tcp            = tcp;
        _stream         = stream;
        ServerCaps      = serverCaps;
        EncodingPolicy  = encodingPolicy;
    }

    /// <summary>
    /// Returns the authenticated transport stream for upper-layer protocol use.
    /// The stream is owned by this session — do not dispose it directly.
    /// </summary>
    public Stream GetStream() => _stream;

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
    }
}
