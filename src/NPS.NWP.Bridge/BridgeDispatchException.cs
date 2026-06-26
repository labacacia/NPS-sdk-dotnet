// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.Bridge;

/// <summary>
/// Exception raised when a Bridge Node cannot parse, route, or execute a bridge
/// invocation. <see cref="ErrorCode"/> carries the NWP-compatible failure code.
/// </summary>
public sealed class BridgeDispatchException : Exception
{
    /// <summary>NWP-compatible error code for the failed dispatch.</summary>
    public string ErrorCode { get; }

    /// <summary>Create a Bridge dispatch exception.</summary>
    public BridgeDispatchException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Create a Bridge dispatch exception with an inner cause.</summary>
    public BridgeDispatchException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
