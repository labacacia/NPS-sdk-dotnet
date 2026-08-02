// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using NPS.Core.Exceptions;

namespace NPS.Core.Ncp;

/// <summary>
/// NCP v0.10 native-mode session continuity across Anchor failover (NPS-CR-0009): on
/// connection loss or an <c>NCP-NID-MISMATCH</c> rejection, the client MUST re-resolve the
/// cluster (NDP highest <c>cluster_epoch</c> / the NWP <c>anchor_failover</c> successor) and
/// re-establish against the new active Anchor. This connector packages that loop: resolution
/// and connection are injected delegates, so it composes with <see cref="NcpNativeClient"/>,
/// TLS wrappers, or tests alike.
/// </summary>
public sealed class NcpFailoverConnector<TSession>(
    Func<CancellationToken, Task<(string Host, int Port)>> resolveActive,
    Func<string, int, CancellationToken, Task<TSession>> connect,
    int maxAttempts = 2)
{
    private readonly Func<CancellationToken, Task<(string Host, int Port)>> _resolveActive =
        resolveActive ?? throw new ArgumentNullException(nameof(resolveActive));
    private readonly Func<string, int, CancellationToken, Task<TSession>> _connect =
        connect ?? throw new ArgumentNullException(nameof(connect));
    private readonly int _maxAttempts = maxAttempts >= 1 ? maxAttempts
        : throw new ArgumentOutOfRangeException(nameof(maxAttempts));

    /// <summary>
    /// Resolve the active Anchor and connect. On a failover-shaped failure (socket/IO loss or
    /// <c>NCP-NID-MISMATCH</c>) the endpoint is re-resolved and the connection retried, up to
    /// <c>maxAttempts</c> total attempts. Non-failover failures propagate immediately.
    /// </summary>
    public async Task<TSession> ConnectAsync(CancellationToken ct = default)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (host, port) = await _resolveActive(ct).ConfigureAwait(false);
            try
            {
                return await _connect(host, port, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFailoverShaped(ex))
            {
                last = ex; // stale endpoint / superseded leader — re-resolve and retry
            }
        }
        throw last!;
    }

    private static bool IsFailoverShaped(Exception ex) => ex switch
    {
        SocketException or IOException => true,
        NpsException npse => npse.ProtocolErrorCode == NcpErrorCodes.NidMismatch,
        _ => false,
    };
}
