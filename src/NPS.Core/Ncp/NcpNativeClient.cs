// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using NPS.Core.Codecs;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Registry;

namespace NPS.Core.Ncp;

/// <summary>
/// NCP native-mode TCP client. Performs the 3-step handshake
/// (preamble → HelloFrame → NcpHandshakeCapsFrame) per NPS-1 §4.6
/// and returns a live <see cref="NcpSession"/>.
/// </summary>
public sealed class NcpNativeClient
{
    private static readonly FrameRegistry HandshakeRegistry =
        new FrameRegistryBuilder()
            .AddNcpHandshake()
            .Build();

    private static readonly Tier1JsonCodec JsonCodec = new();
    private static readonly Tier2MsgPackCodec MsgPackCodec = new();
    private static readonly Tier3BinaryVectorCodec BinaryVectorCodec = new();

    private readonly NpsFrameCodec _codec;

    /// <param name="codec">Codec instance used to encode the outbound <see cref="HelloFrame"/>.</param>
    public NcpNativeClient(NpsFrameCodec codec) => _codec = codec;

    /// <summary>
    /// Opens a TCP connection to <paramref name="host"/>:<paramref name="port"/>, performs
    /// the NCP native-mode handshake, and returns a live session.
    /// </summary>
    /// <exception cref="NcpHandshakeException">Server rejected the handshake or sent an unexpected frame.</exception>
    public async Task<NcpSession> ConnectAsync(
        string host,
        int port,
        HelloFrame hello,
        CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        var stream = tcp.GetStream();
        try
        {
            // 1 — preamble (Tier-1 JSON encoding not yet negotiated)
            await NcpPreamble.WriteAsync(stream, ct).ConfigureAwait(false);

            // 2 — HelloFrame (always JSON per spec — encoding not yet agreed)
            var helloWire = _codec.Encode(hello, EncodingTier.Json);
            await stream.WriteAsync(helloWire, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            // 3 — read server response header (handles EXT flag)
            var (header, _) = await ReadFrameHeaderAsync(stream, ct).ConfigureAwait(false);

            // 4 — read payload
            var payload = new byte[header.PayloadLength];
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

            // 5 — ErrorFrame → throw
            if (header.FrameType == FrameType.Error)
            {
                var err = (ErrorFrame)DecodeHandshakeFrame(header, payload);
                throw new NcpHandshakeException(err.Error, err.Message);
            }

            if (header.FrameType != FrameType.Caps)
                throw new NcpHandshakeException(
                    "NCP-HANDSHAKE-UNEXPECTED-FRAME",
                    $"Expected CapsFrame (0x{(byte)FrameType.Caps:X2}), got 0x{(byte)header.FrameType:X2}.");

            // 6 — decode NcpHandshakeCapsFrame using the negotiated tier the server
            // signalled as the stable default in the response header flags.
            var negotiatedTier = header.EncodingTier;
            var caps = (NcpHandshakeCapsFrame)DecodeHandshakeFrame(header, payload);
            var policy = NcpEncodingPolicy.FromEnabledEncodings(negotiatedTier, caps.EnabledEncodings);

            return new NcpSession(tcp, stream, caps, policy);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            tcp.Dispose();
            throw;
        }
    }

    private static IFrame DecodeHandshakeFrame(FrameHeader header, ReadOnlySpan<byte> payload) =>
        header.EncodingTier switch
        {
            EncodingTier.Json => JsonCodec.Decode(header.FrameType, payload, HandshakeRegistry),
            EncodingTier.MsgPack => MsgPackCodec.Decode(header.FrameType, payload, HandshakeRegistry),
            EncodingTier.BinaryVector => BinaryVectorCodec.Decode(header.FrameType, payload, HandshakeRegistry),
            _ => throw new NpsCodecException($"Unsupported handshake encoding tier: {header.EncodingTier} (0x{(byte)header.EncodingTier:X2}).")
        };

    /// <summary>
    /// Reads a frame header from <paramref name="stream"/>, peeking the EXT flag
    /// to determine whether to read 4 or 8 bytes total.
    /// </summary>
    internal static async Task<(FrameHeader header, byte[] raw)> ReadFrameHeaderAsync(
        Stream stream, CancellationToken ct)
    {
        // Always read 2 bytes first to detect EXT flag
        var peek = new byte[2];
        await stream.ReadExactlyAsync(peek, ct).ConfigureAwait(false);

        bool ext = ((FrameFlags)peek[1] & FrameFlags.Ext) != 0;
        int remaining = ext ? FrameHeader.ExtendedSize - 2 : FrameHeader.DefaultSize - 2;

        var rest = new byte[remaining];
        await stream.ReadExactlyAsync(rest, ct).ConfigureAwait(false);

        var raw = new byte[peek.Length + rest.Length];
        peek.CopyTo(raw, 0);
        rest.CopyTo(raw, peek.Length);

        return (FrameHeader.Parse(raw), raw);
    }
}
