// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NDP.Registry;

/// <summary>
/// Thrown by <see cref="InMemoryNdpRegistry.Announce"/> when the announce frame
/// violates the configured security profile (NPS-4 §7.2).
/// </summary>
public sealed class NdpAnnounceRejectedException : Exception
{
    /// <summary>NDP error code identifying the violation.</summary>
    public string ErrorCode { get; }

    /// <summary>Creates a new instance with the given error code and message.</summary>
    public NdpAnnounceRejectedException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}
