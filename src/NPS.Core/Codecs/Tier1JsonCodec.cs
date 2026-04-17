// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Registry;

namespace NPS.Core.Codecs;

// ── IFrameCodec ───────────────────────────────────────────────────────────────

/// <summary>Low-level encode/decode contract for a specific encoding tier.</summary>
public interface IFrameCodec
{
    byte[]  Encode(IFrame frame);
    IFrame  Decode(FrameType type, ReadOnlySpan<byte> payload, FrameRegistry registry);
}

// ── Tier1JsonCodec ────────────────────────────────────────────────────────────

/// <summary>
/// Tier-1 codec: UTF-8 JSON serialisation via <c>System.Text.Json</c>.
/// Used in development, debugging, and compatibility mode.
/// </summary>
public sealed class Tier1JsonCodec : IFrameCodec
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = false,
    };

    public byte[] Encode(IFrame frame)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(frame, frame.GetType(), _opts);
        }
        catch (Exception ex)
        {
            throw new NpsCodecException($"Tier-1 JSON encode failed for {frame.FrameType}.", ex);
        }
    }

    public IFrame Decode(FrameType type, ReadOnlySpan<byte> payload, FrameRegistry registry)
    {
        var clrType = registry.Resolve(type);
        try
        {
            return (IFrame)JsonSerializer.Deserialize(payload, clrType, _opts)!;
        }
        catch (Exception ex)
        {
            throw new NpsCodecException($"Tier-1 JSON decode failed for {type}.", ex);
        }
    }
}
