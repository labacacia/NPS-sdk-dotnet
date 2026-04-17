// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Registry;

/// <summary>
/// Maps <see cref="FrameType"/> byte codes to CLR types for the codec layer.
/// Built once at startup via <see cref="FrameRegistryBuilder"/>, then frozen — thread-safe reads with no locking.
/// Upper-layer protocols (NWP, NIP, …) register their own frame types via <c>AddNwp()</c> etc.
/// </summary>
public sealed class FrameRegistry
{
    private readonly FrozenDictionary<FrameType, Type> _map;

    internal FrameRegistry(Dictionary<FrameType, Type> map) =>
        _map = map.ToFrozenDictionary();

    /// <summary>Resolves a <see cref="FrameType"/> to its CLR record type.</summary>
    /// <exception cref="NpsFrameException">Thrown for unregistered frame types.</exception>
    public Type Resolve(FrameType type) =>
        _map.TryGetValue(type, out var t)
            ? t
            : throw new NpsFrameException(
                $"No CLR type registered for FrameType 0x{(byte)type:X2} ({type}). " +
                $"Register it via FrameRegistryBuilder or the corresponding AddNxx() extension.");

    /// <summary>Creates a registry pre-populated with all NCP core frames.</summary>
    public static FrameRegistry CreateDefault()
    {
#pragma warning disable CS0618 // AlignFrame retained for backward-compat with NCP v0.1 peers
        return new FrameRegistryBuilder()
            .Register<AnchorFrame>(FrameType.Anchor)
            .Register<DiffFrame>  (FrameType.Diff)
            .Register<StreamFrame>(FrameType.Stream)
            .Register<CapsFrame>  (FrameType.Caps)
            .Register<AlignFrame> (FrameType.Align)   // Deprecated: use NOP AlignStream (0x43)
            .Register<HelloFrame> (FrameType.Hello)
            .Register<ErrorFrame> (FrameType.Error)
            .Build();
#pragma warning restore CS0618
    }
}

/// <summary>
/// Fluent builder for <see cref="FrameRegistry"/>.
/// Called by <c>AddNpsCore()</c> for NCP frames and by upper-layer
/// <c>AddNwp()</c> / <c>AddNip()</c> etc. for protocol-specific frames.
/// </summary>
public sealed class FrameRegistryBuilder
{
    private readonly Dictionary<FrameType, Type> _map = new();

    /// <summary>Registers <typeparamref name="T"/> for the given <see cref="FrameType"/> code.</summary>
    public FrameRegistryBuilder Register<T>(FrameType type) where T : IFrame
    {
        _map[type] = typeof(T);
        return this;
    }

    public FrameRegistry Build() => new(_map);

    /// <summary>
    /// Registers all NCP core frame types.
    /// Upper-layer registrations (NWP, NIP, …) chain after this call.
    /// </summary>
    public FrameRegistryBuilder AddNcp()
    {
#pragma warning disable CS0618 // AlignFrame retained for backward-compat
        Register<AnchorFrame>(FrameType.Anchor);
        Register<DiffFrame>  (FrameType.Diff);
        Register<StreamFrame>(FrameType.Stream);
        Register<CapsFrame>  (FrameType.Caps);
        Register<AlignFrame> (FrameType.Align);
        Register<HelloFrame> (FrameType.Hello);
        Register<ErrorFrame> (FrameType.Error);
#pragma warning restore CS0618
        return this;
    }
}
