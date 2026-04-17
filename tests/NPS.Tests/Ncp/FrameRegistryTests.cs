// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Registry;

namespace NPS.Tests.Ncp;

public sealed class FrameRegistryTests
{
    [Theory]
    [InlineData(FrameType.Anchor, typeof(AnchorFrame))]
    [InlineData(FrameType.Diff,   typeof(DiffFrame))]
    [InlineData(FrameType.Stream, typeof(StreamFrame))]
    [InlineData(FrameType.Caps,   typeof(CapsFrame))]
    [InlineData(FrameType.Error,  typeof(ErrorFrame))]
    public void CreateDefault_RegistersNcpFrameTypes(FrameType type, Type expected)
    {
        var registry = FrameRegistry.CreateDefault();
        Assert.Equal(expected, registry.Resolve(type));
    }

    [Fact]
    public void Resolve_UnregisteredType_ThrowsNpsFrameException()
    {
        var registry = FrameRegistry.CreateDefault();
        // 0xAA is not a registered frame type
        var ex = Assert.Throws<NpsFrameException>(() => registry.Resolve((FrameType)0xAA));
        Assert.Contains("0xAA", ex.Message);
    }

    [Fact]
    public void Builder_RegisterOverwrite_UsesLastRegistration()
    {
        // Registering the same type twice should overwrite (last wins)
        var registry = new FrameRegistryBuilder()
            .Register<AnchorFrame>(FrameType.Anchor)
            .Register<DiffFrame>  (FrameType.Anchor) // overwrite
            .Build();

        Assert.Equal(typeof(DiffFrame), registry.Resolve(FrameType.Anchor));
    }
}
