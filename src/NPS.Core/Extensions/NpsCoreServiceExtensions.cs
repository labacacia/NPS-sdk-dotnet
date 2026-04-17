// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using NPS.Core.Caching;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Registry;

namespace NPS.Core.Extensions;

/// <summary>Configuration options for the NPS.Core layer.</summary>
public sealed class NpsCoreOptions
{
    /// <summary>
    /// Default encoding tier used when a frame does not specify <c>PreferredTier</c>.
    /// Defaults to <see cref="EncodingTier.MsgPack"/> (production).
    /// Set to <see cref="EncodingTier.Json"/> for development / debugging.
    /// </summary>
    public EncodingTier DefaultTier { get; set; } = EncodingTier.MsgPack;

    /// <summary>
    /// Default TTL in seconds for AnchorFrame cache entries when the frame
    /// itself does not specify <c>Ttl</c>. Default: 3600 (1 hour).
    /// </summary>
    public int AnchorTtlSeconds { get; set; } = 3600;

    /// <summary>
    /// When <c>true</c>, plaintext (non-TLS) connections are permitted.
    /// Must be <c>false</c> in production (NPS-1 §7).
    /// </summary>
    public bool AllowPlaintext { get; set; } = false;

    /// <summary>
    /// Maximum payload size in bytes for a single frame (NPS-1 §3.3).
    /// Default: 65535 (64 KiB, fits in a 4-byte header).
    /// Set higher to enable EXT mode (8-byte header, up to 4 GB).
    /// </summary>
    public uint MaxFramePayload { get; set; } = FrameHeader.DefaultMaxPayload;

    /// <summary>
    /// When <c>true</c>, the codec will use the 8-byte extended frame header
    /// for payloads exceeding 64 KiB. When <c>false</c>, payloads exceeding
    /// 64 KiB will throw and must use StreamFrame fragmentation.
    /// </summary>
    public bool EnableExtendedFrameHeader { get; set; } = false;
}

/// <summary>DI registration extensions for NPS.Core.</summary>
public static class NpsCoreServiceExtensions
{
    /// <summary>
    /// Registers NPS.Core services into the DI container:
    /// <list type="bullet">
    ///   <item><see cref="FrameRegistry"/> (singleton) — NCP frames pre-registered.</item>
    ///   <item><see cref="Tier1JsonCodec"/> (singleton)</item>
    ///   <item><see cref="Tier2MsgPackCodec"/> (singleton)</item>
    ///   <item><see cref="NpsFrameCodec"/> (singleton)</item>
    ///   <item><see cref="AnchorFrameCache"/> (scoped — per connection/session)</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddNpsCore(
        this IServiceCollection services,
        Action<NpsCoreOptions>? configure = null)
    {
        var options = new NpsCoreOptions();
        configure?.Invoke(options);

        services.AddMemoryCache();

        services.AddSingleton<FrameRegistry>(_ => FrameRegistry.CreateDefault());
        services.AddSingleton<Tier1JsonCodec>();
        services.AddSingleton<Tier2MsgPackCodec>();

        var maxPayload = options.EnableExtendedFrameHeader
            ? options.MaxFramePayload
            : (uint)FrameHeader.DefaultMaxPayload;

        services.AddSingleton(sp => new NpsFrameCodec(
            sp.GetRequiredService<Tier1JsonCodec>(),
            sp.GetRequiredService<Tier2MsgPackCodec>(),
            sp.GetRequiredService<FrameRegistry>(),
            maxPayload));

        services.AddScoped<AnchorFrameCache>();
        services.AddSingleton(options);

        return services;
    }
}
