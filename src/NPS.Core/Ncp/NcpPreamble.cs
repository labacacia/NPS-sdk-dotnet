// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using NPS.Core.Exceptions;

namespace NPS.Core.Ncp;

/// <summary>
/// NCP native-mode connection preamble — the 8-byte ASCII constant
/// <c>b"NPS/1.0\n"</c> that every native-mode client MUST emit immediately
/// after the transport (TCP / QUIC) handshake and before its first
/// <c>HelloFrame</c>. Defined by NPS-RFC-0001 and NPS-1 NCP §2.6.1.
/// <para>
/// HTTP-mode connections do <b>not</b> use the preamble — the
/// <c>Content-Type: application/nwp-frame</c> header serves the same
/// identification role.
/// </para>
/// <para>
/// This class is library-only — it does not own a transport. SDKs and
/// third-party native transports use the constants and helpers here to
/// emit the preamble on the client side, validate it on the server side,
/// and classify mismatched connections via <see cref="NpsStatusCodes.ProtoPreambleInvalid"/>.
/// </para>
/// </summary>
public static class NcpPreamble
{
    /// <summary>The literal preamble string, including the trailing LF.</summary>
    public const string Literal = "NPS/1.0\n";

    /// <summary>
    /// Length of the preamble in bytes. A native-mode server MUST read
    /// exactly this many bytes before doing anything else with the connection.
    /// </summary>
    public const int Length = 8;

    /// <summary>
    /// Validation timeout per NPS-RFC-0001 §4.1: if fewer than
    /// <see cref="Length"/> bytes arrive within this window, the server MUST
    /// close the connection silently.
    /// </summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum delay between deciding a preamble is invalid and closing the
    /// underlying transport, per NPS-RFC-0001 §4.1.
    /// </summary>
    public static readonly TimeSpan CloseDeadline = TimeSpan.FromMilliseconds(500);

    /// <summary>NCP error code for an invalid preamble.</summary>
    public const string ErrorCode = NcpErrorCodes.PreambleInvalid;

    /// <summary>NPS status code for an invalid preamble (PROTO category).</summary>
    public const string StatusCode = NpsStatusCodes.ProtoPreambleInvalid;

    private static readonly byte[] _bytes = Encoding.ASCII.GetBytes(Literal);

    /// <summary>
    /// The constant preamble bytes <c>4E 50 53 2F 31 2E 30 0A</c>
    /// (<c>"NPS/1.0\n"</c>). The returned span is over a private buffer
    /// — copy if a mutable result is required.
    /// </summary>
    public static ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>
    /// Returns a fresh copy of the preamble bytes. Useful when the caller
    /// needs an owned <c>byte[]</c> rather than a span over the shared buffer.
    /// </summary>
    public static byte[] ToArray() => (byte[])_bytes.Clone();

    /// <summary>
    /// Returns <c>true</c> iff <paramref name="buffer"/> begins with the
    /// 8-byte NPS/1.0 preamble. Safe to call with shorter buffers
    /// (returns <c>false</c>).
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> buffer)
        => buffer.Length >= Length && buffer.Slice(0, Length).SequenceEqual(Bytes);

    /// <summary>
    /// Validates a presumed-preamble buffer.
    /// </summary>
    /// <param name="buffer">
    /// Bytes received from the connection. MAY be shorter than
    /// <see cref="Length"/> — callers should treat short reads as failure.
    /// </param>
    /// <param name="reason">
    /// On failure, a short, human-readable reason suitable for log lines
    /// (never null when the return value is <c>false</c>).
    /// </param>
    /// <returns>
    /// <c>true</c> if the buffer matches the preamble exactly. <c>false</c>
    /// for short reads, length mismatches, or content mismatches.
    /// </returns>
    public static bool TryValidate(ReadOnlySpan<byte> buffer, out string reason)
    {
        if (buffer.Length < Length)
        {
            reason = $"short read ({buffer.Length}/{Length} bytes); peer is not speaking NCP";
            return false;
        }

        if (!buffer.Slice(0, Length).SequenceEqual(Bytes))
        {
            // Distinguish a future-major-version preamble (peer self-identified
            // as an NPS speaker but with a major we do not support) from
            // arbitrary garbage. RFC-0001 §4.1 permits a one-time diagnostic
            // line for the former; we surface it via the reason string so the
            // calling transport can decide whether to write the diagnostic.
            if (buffer.Length >= 4 && buffer[0] == (byte)'N' && buffer[1] == (byte)'P'
                                   && buffer[2] == (byte)'S' && buffer[3] == (byte)'/')
            {
                reason = "future-major-version NPS preamble; close with NPS-PREAMBLE-UNSUPPORTED-VERSION diagnostic";
            }
            else
            {
                reason = "preamble mismatch; peer is not speaking NPS/1.x";
            }
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates a presumed-preamble buffer, throwing
    /// <see cref="NcpPreambleInvalidException"/> on mismatch. Convenience
    /// wrapper over <see cref="TryValidate"/> for code paths that prefer
    /// exceptions to out-parameters.
    /// </summary>
    public static void Validate(ReadOnlySpan<byte> buffer)
    {
        if (!TryValidate(buffer, out var reason))
            throw new NcpPreambleInvalidException(reason);
    }

    /// <summary>
    /// Writes the preamble to <paramref name="stream"/> in a single call.
    /// Intended to be called once, immediately after the transport
    /// handshake completes and before the first frame.
    /// </summary>
    public static ValueTask WriteAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.WriteAsync(_bytes.AsMemory(), ct);
    }
}
