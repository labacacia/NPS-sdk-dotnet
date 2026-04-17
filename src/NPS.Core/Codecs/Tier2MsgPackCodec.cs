// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using MessagePack;
using MessagePack.Resolvers;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Registry;

namespace NPS.Core.Codecs;

/// <summary>
/// Tier-2 codec: MessagePack binary serialisation via MessagePack-CSharp.
/// Produces ~60 % smaller payloads vs Tier-1 JSON; default for production.
/// Uses <see cref="ContractlessStandardResolver"/> to avoid [MessagePackObject] annotations
/// on frame records — standard property names are used as keys.
/// </summary>
public sealed class Tier2MsgPackCodec : IFrameCodec
{
    private static readonly MessagePackSerializerOptions _opts =
        MessagePackSerializerOptions.Standard
            .WithResolver(ContractlessStandardResolver.Instance)
            .WithCompression(MessagePackCompression.None);  // Compression handled at FrameFlags level

    public byte[] Encode(IFrame frame)
    {
        try
        {
            return MessagePackSerializer.Serialize(frame.GetType(), frame, _opts);
        }
        catch (Exception ex)
        {
            throw new NpsCodecException($"Tier-2 MsgPack encode failed for {frame.FrameType}.", ex);
        }
    }

    public IFrame Decode(FrameType type, ReadOnlySpan<byte> payload, FrameRegistry registry)
    {
        var clrType = registry.Resolve(type);
        try
        {
            var sequence = new System.Buffers.ReadOnlySequence<byte>(payload.ToArray());
            var reader = new MessagePackReader(sequence);
            return (IFrame)MessagePackSerializer.Deserialize(clrType, ref reader, _opts)!;
        }
        catch (Exception ex)
        {
            throw new NpsCodecException($"Tier-2 MsgPack decode failed for {type}.", ex);
        }
    }
}
