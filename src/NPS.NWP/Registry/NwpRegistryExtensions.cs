// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Frames;
using NPS.Core.Registry;
using NPS.NWP.Frames;
using NPS.NWP.Serialization;

namespace NPS.NWP.Registry;

/// <summary>NativeAOT-safe NWP frame registrations.</summary>
public static class NwpRegistryExtensions
{
    /// <summary>Adds all NWP frame codecs to an existing frame registry builder.</summary>
    public static FrameRegistryBuilder AddNwp(this FrameRegistryBuilder builder) =>
        builder
            .Register(FrameType.Query, NwpJsonContext.Default.QueryFrame, NwpMessagePackResolver.Instance)
            .Register(FrameType.Action, NwpJsonContext.Default.ActionFrame, NwpMessagePackResolver.Instance)
            .Register(FrameType.Subscribe, NwpJsonContext.Default.SubscribeFrame, NwpMessagePackResolver.Instance);
}
