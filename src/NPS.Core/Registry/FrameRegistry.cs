// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.Core.Serialization;

namespace NPS.Core.Registry;

/// <summary>
/// Maps <see cref="FrameType"/> byte codes to CLR types for the codec layer.
/// Built once at startup via <see cref="FrameRegistryBuilder"/>, then frozen — thread-safe reads with no locking.
/// Upper-layer protocols (NWP, NIP, …) register their own frame types via <c>AddNwp()</c> etc.
/// </summary>
public sealed class FrameRegistry
{
    private readonly FrozenDictionary<FrameType, FrameCodecRegistration> _map;
    private readonly FrozenDictionary<Type, FrameCodecRegistration> _encoders;

    internal FrameRegistry(
        Dictionary<FrameType, FrameCodecRegistration> map,
        Dictionary<Type, FrameCodecRegistration> encoders)
    {
        _map = map.ToFrozenDictionary();
        _encoders = encoders.ToFrozenDictionary();
    }

    /// <summary>Resolves a <see cref="FrameType"/> to its CLR record type.</summary>
    /// <exception cref="NpsFrameException">Thrown for unregistered frame types.</exception>
    public Type Resolve(FrameType type) =>
        ResolveRegistration(type).ClrType;

    internal FrameCodecRegistration ResolveRegistration(FrameType type) =>
        _map.TryGetValue(type, out var registration)
            ? registration
            : throw new NpsFrameException(
                $"No CLR type registered for FrameType 0x{(byte)type:X2} ({type}). " +
                $"Register it via FrameRegistryBuilder or the corresponding AddNxx() extension.");

    internal FrameCodecRegistration ResolveRegistration(IFrame frame) =>
        _encoders.TryGetValue(frame.GetType(), out var registration)
            ? registration
            : throw new NpsFrameException(
                $"No codec metadata registered for CLR frame type {frame.GetType().FullName}. " +
                "Register it via FrameRegistryBuilder or the corresponding AddNxx() extension.");

    /// <summary>Creates a registry pre-populated with all NCP core frames.</summary>
    public static FrameRegistry CreateDefault()
    {
#pragma warning disable CS0618 // AlignFrame retained for backward-compat with NCP v0.1 peers
        return new FrameRegistryBuilder()
            .Register(FrameType.Anchor, NpsCoreJsonContext.Default.AnchorFrame, NpsMessagePackResolver.Instance)
            .Register(FrameType.Diff, NpsCoreJsonContext.Default.DiffFrame, NpsMessagePackResolver.Instance)
            .Register(FrameType.Stream, NpsCoreJsonContext.Default.StreamFrame, NpsMessagePackResolver.Instance)
            .Register(FrameType.Caps, NpsCoreJsonContext.Default.CapsFrame, NpsMessagePackResolver.Instance)
            .Register(FrameType.Align, NpsCoreJsonContext.Default.AlignFrame, NpsMessagePackResolver.Instance)
            .Register(FrameType.Hello, NpsCoreJsonContext.Default.HelloFrame, NpsMessagePackResolver.Instance)
            .Register(FrameType.Error, NpsCoreJsonContext.Default.ErrorFrame, NpsMessagePackResolver.Instance)
            .RegisterEncoder(NpsCoreJsonContext.Default.NcpHandshakeCapsFrame, NpsMessagePackResolver.Instance)
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
    private readonly Dictionary<FrameType, FrameCodecRegistration> _map = new();
    private readonly Dictionary<Type, FrameCodecRegistration> _encoders = new();

    /// <summary>
    /// Registers <typeparamref name="T"/> using runtime-generated JSON and MessagePack metadata.
    /// Prefer the metadata overload for NativeAOT-compatible applications.
    /// </summary>
    [RequiresDynamicCode("Runtime serializer metadata generation is not compatible with NativeAOT.")]
    [RequiresUnreferencedCode("Runtime serializer metadata generation requires members that trimming may remove.")]
    public FrameRegistryBuilder Register<T>(FrameType type) where T : IFrame
    {
        var registration = FrameCodecRegistration.CreateDynamic<T>();
        _map[type] = registration;
        _encoders[typeof(T)] = registration;
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="T"/> with source-generated JSON and MessagePack metadata.
    /// </summary>
    public FrameRegistryBuilder Register<T>(
        FrameType type,
        JsonTypeInfo<T> jsonTypeInfo,
        IFormatterResolver messagePackResolver)
        where T : IFrame
    {
        var registration = FrameCodecRegistration.Create(jsonTypeInfo, messagePackResolver);
        _map[type] = registration;
        _encoders[typeof(T)] = registration;
        return this;
    }

    /// <summary>
    /// Adds encode metadata for a CLR frame type that shares its wire code with another frame.
    /// </summary>
    public FrameRegistryBuilder RegisterEncoder<T>(
        JsonTypeInfo<T> jsonTypeInfo,
        IFormatterResolver messagePackResolver)
        where T : IFrame
    {
        _encoders[typeof(T)] = FrameCodecRegistration.Create(jsonTypeInfo, messagePackResolver);
        return this;
    }

    public FrameRegistry Build() => new(_map, _encoders);

    /// <summary>
    /// Registers all NCP core frame types.
    /// Upper-layer registrations (NWP, NIP, …) chain after this call.
    /// </summary>
    public FrameRegistryBuilder AddNcp()
    {
#pragma warning disable CS0618 // AlignFrame retained for backward-compat
        Register(FrameType.Anchor, NpsCoreJsonContext.Default.AnchorFrame, NpsMessagePackResolver.Instance);
        Register(FrameType.Diff, NpsCoreJsonContext.Default.DiffFrame, NpsMessagePackResolver.Instance);
        Register(FrameType.Stream, NpsCoreJsonContext.Default.StreamFrame, NpsMessagePackResolver.Instance);
        Register(FrameType.Caps, NpsCoreJsonContext.Default.CapsFrame, NpsMessagePackResolver.Instance);
        Register(FrameType.Align, NpsCoreJsonContext.Default.AlignFrame, NpsMessagePackResolver.Instance);
        Register(FrameType.Hello, NpsCoreJsonContext.Default.HelloFrame, NpsMessagePackResolver.Instance);
        Register(FrameType.Error, NpsCoreJsonContext.Default.ErrorFrame, NpsMessagePackResolver.Instance);
        RegisterEncoder(NpsCoreJsonContext.Default.NcpHandshakeCapsFrame, NpsMessagePackResolver.Instance);
#pragma warning restore CS0618
        return this;
    }

    /// <summary>
    /// Registers the native NCP handshake wire views. The Caps wire code resolves to
    /// <see cref="NcpHandshakeCapsFrame"/> instead of the ordinary <see cref="CapsFrame"/>.
    /// </summary>
    public FrameRegistryBuilder AddNcpHandshake()
    {
        Register(FrameType.Hello, NpsCoreJsonContext.Default.HelloFrame, NpsMessagePackResolver.Instance);
        Register(
            FrameType.Caps,
            NpsCoreJsonContext.Default.NcpHandshakeCapsFrame,
            NpsMessagePackResolver.Instance);
        Register(FrameType.Error, NpsCoreJsonContext.Default.ErrorFrame, NpsMessagePackResolver.Instance);
        return this;
    }
}

internal delegate IFrame FramePayloadDecoder(ReadOnlySpan<byte> payload);

internal sealed class FrameCodecRegistration
{
    private FrameCodecRegistration(
        Type clrType,
        Func<IFrame, byte[]> jsonEncoder,
        FramePayloadDecoder jsonDecoder,
        Func<IFrame, byte[]> messagePackEncoder,
        FramePayloadDecoder messagePackDecoder)
    {
        ClrType = clrType;
        JsonEncoder = jsonEncoder;
        JsonDecoder = jsonDecoder;
        MessagePackEncoder = messagePackEncoder;
        MessagePackDecoder = messagePackDecoder;
    }

    internal Type ClrType { get; }

    internal Func<IFrame, byte[]> JsonEncoder { get; }

    internal FramePayloadDecoder JsonDecoder { get; }

    internal Func<IFrame, byte[]> MessagePackEncoder { get; }

    internal FramePayloadDecoder MessagePackDecoder { get; }

    internal static FrameCodecRegistration Create<T>(
        JsonTypeInfo<T> jsonTypeInfo,
        IFormatterResolver messagePackResolver)
        where T : IFrame
    {
        var messagePackOptions = new MessagePackSerializerOptions(messagePackResolver);
        var messagePackFormatter = messagePackResolver.GetFormatter<T>()
                                   ?? throw new InvalidOperationException(
                                       $"No source-generated MessagePack formatter registered for {typeof(T).FullName}.");

        return new FrameCodecRegistration(
            typeof(T),
            frame => JsonSerializer.SerializeToUtf8Bytes((T)frame, jsonTypeInfo),
            payload => JsonSerializer.Deserialize(payload, jsonTypeInfo)!,
            frame => EncodeMessagePack((T)frame, messagePackFormatter, messagePackOptions),
            payload => DecodeMessagePack(payload, messagePackFormatter, messagePackOptions));
    }

    private static byte[] EncodeMessagePack<T>(
        T frame,
        IMessagePackFormatter<T> formatter,
        MessagePackSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        formatter.Serialize(ref writer, frame, options);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static IFrame DecodeMessagePack<T>(
        ReadOnlySpan<byte> payload,
        IMessagePackFormatter<T> formatter,
        MessagePackSerializerOptions options)
        where T : IFrame
    {
        var sequence = new ReadOnlySequence<byte>(payload.ToArray());
        var reader = new MessagePackReader(sequence);
        var frame = formatter.Deserialize(ref reader, options);
        if (!reader.End)
            throw new MessagePackSerializationException(
                $"MessagePack payload for {typeof(T).FullName} has trailing values.");
        return frame;
    }

    [RequiresDynamicCode("Runtime serializer metadata generation is not compatible with NativeAOT.")]
    [RequiresUnreferencedCode("Runtime serializer metadata generation requires members that trimming may remove.")]
    internal static FrameCodecRegistration CreateDynamic<T>() where T : IFrame =>
        new(
            typeof(T),
            frame => JsonSerializer.SerializeToUtf8Bytes(frame, typeof(T), DynamicFallback.JsonOptions),
            payload => (IFrame)JsonSerializer.Deserialize(payload, typeof(T), DynamicFallback.JsonOptions)!,
            frame => MessagePackSerializer.Serialize(typeof(T), frame, DynamicFallback.MessagePackOptions),
            payload => (IFrame)MessagePackSerializer.Deserialize(
                typeof(T),
                payload.ToArray(),
                DynamicFallback.MessagePackOptions)!);

    private static class DynamicFallback
    {
        internal static JsonSerializerOptions JsonOptions { get; } = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };

        internal static MessagePackSerializerOptions MessagePackOptions { get; } =
            MessagePackSerializerOptions.Standard
                .WithResolver(ContractlessStandardResolver.Instance)
                .WithCompression(MessagePackCompression.None);
    }
}
