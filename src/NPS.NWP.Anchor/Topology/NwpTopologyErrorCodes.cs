// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.Anchor.Topology;

/// <summary>
/// NWP topology error codes from NPS-2 §13 (added by NPS-CR-0002). Kept in this
/// assembly rather than <c>NPS.NWP.Http.NwpErrorCodes</c> because they are
/// only meaningful when the Anchor has registered an
/// <see cref="IAnchorTopologyService"/>.
/// </summary>
public static class NwpTopologyErrorCodes
{
    /// <summary>NWP-TOPOLOGY-UNAUTHORIZED → NPS-AUTH-FORBIDDEN.</summary>
    public const string Unauthorized = "NWP-TOPOLOGY-UNAUTHORIZED";

    /// <summary>NWP-TOPOLOGY-UNSUPPORTED-SCOPE → NPS-CLIENT-BAD-PARAM.</summary>
    public const string UnsupportedScope = "NWP-TOPOLOGY-UNSUPPORTED-SCOPE";

    /// <summary>NWP-TOPOLOGY-DEPTH-UNSUPPORTED → NPS-CLIENT-BAD-PARAM.</summary>
    public const string DepthUnsupported = "NWP-TOPOLOGY-DEPTH-UNSUPPORTED";

    /// <summary>NWP-TOPOLOGY-FILTER-UNSUPPORTED → NPS-CLIENT-BAD-PARAM.</summary>
    public const string FilterUnsupported = "NWP-TOPOLOGY-FILTER-UNSUPPORTED";
}

/// <summary>
/// Thrown by <see cref="IAnchorTopologyService"/> implementations to signal a
/// topology-specific protocol error. The middleware catches these and emits
/// the corresponding <c>ErrorFrame</c>.
/// </summary>
public sealed class TopologyProtocolException : Exception
{
    public TopologyProtocolException(string nwpErrorCode, string npsStatus, string message)
        : base(message)
    {
        NwpErrorCode = nwpErrorCode;
        NpsStatus    = npsStatus;
    }

    /// <summary>The NWP error code (e.g. <c>NWP-TOPOLOGY-UNSUPPORTED-SCOPE</c>).</summary>
    public string NwpErrorCode { get; }

    /// <summary>The NPS status code (e.g. <c>NPS-CLIENT-BAD-PARAM</c>).</summary>
    public string NpsStatus { get; }
}
