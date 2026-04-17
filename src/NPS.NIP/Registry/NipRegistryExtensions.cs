// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Frames;
using NPS.Core.Registry;
using NPS.NIP.Frames;

namespace NPS.NIP.Registry;

/// <summary>
/// Extension methods that register NIP frame types into a <see cref="FrameRegistryBuilder"/>.
/// </summary>
public static class NipRegistryExtensions
{
    /// <summary>
    /// Registers all NIP frame types (IdentFrame, TrustFrame, RevokeFrame) with the builder.
    /// Call this after the NCP defaults to enable NCP codec routing of NIP frames:
    /// <code>
    /// var registry = new FrameRegistryBuilder()
    ///     .AddNcp()
    ///     .AddNip()
    ///     .Build();
    /// </code>
    /// </summary>
    public static FrameRegistryBuilder AddNip(this FrameRegistryBuilder builder) =>
        builder
            .Register<IdentFrame> (FrameType.Ident)
            .Register<TrustFrame> (FrameType.Trust)
            .Register<RevokeFrame>(FrameType.Revoke);
}
