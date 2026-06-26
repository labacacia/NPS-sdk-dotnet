// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using NPS.Core.Frames;

namespace NPS.Core.Ncp;

/// <summary>
/// Server-side native NCP transport options.
/// </summary>
public sealed class NcpServerOptions
{
    /// <summary>
    /// Optional hook that wraps or authenticates the accepted TCP stream before
    /// the NCP preamble is read. Use this to install TLS/mTLS via
    /// <c>SslStream.AuthenticateAsServerAsync</c>.
    /// </summary>
    public Func<TcpClient, Stream, CancellationToken, ValueTask<Stream>>? AuthenticateStreamAsync { get; init; }

    /// <summary>
    /// When true, <see cref="AuthenticateStreamAsync"/> must return a different
    /// stream instance, making accidental plaintext native mode fail fast.
    /// </summary>
    public bool RequireAuthenticatedStream { get; init; }

    /// <summary>
    /// Maximum payload accepted for the initial <c>HelloFrame</c>. Defaults to
    /// the normal non-extended frame payload ceiling instead of the 4 GiB
    /// extended-frame limit.
    /// </summary>
    public uint MaxHelloPayload { get; init; } = FrameHeader.DefaultMaxPayload;

    /// <summary>
    /// Wall-clock budget for the preamble, frame header, and Hello payload read.
    /// Defaults to the NCP preamble timeout.
    /// </summary>
    public TimeSpan HandshakeReadTimeout { get; init; } = NcpPreamble.ReadTimeout;
}
