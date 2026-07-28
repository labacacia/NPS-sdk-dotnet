// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Frames;
using NPS.Core.Registry;
using NPS.NDP.Frames;
using NPS.NDP.Serialization;

namespace NPS.NDP.Registry;

/// <summary>
/// Extension methods to register NDP frame types into the NCP <see cref="FrameRegistryBuilder"/>.
/// </summary>
public static class NdpRegistryExtensions
{
    /// <summary>
    /// Registers all NDP frame types (Announce 0x30, Resolve 0x31, Graph 0x32)
    /// into the builder. Chain after <c>AddNcp()</c> and before <c>Build()</c>.
    /// </summary>
    public static FrameRegistryBuilder AddNdp(this FrameRegistryBuilder builder) =>
        builder
            .Register(FrameType.Announce, NdpJsonContext.Default.AnnounceFrame, NdpMessagePackResolver.Instance)
            .Register(FrameType.Resolve, NdpJsonContext.Default.ResolveFrame, NdpMessagePackResolver.Instance)
            .Register(FrameType.Graph, NdpJsonContext.Default.GraphFrame, NdpMessagePackResolver.Instance);
}
