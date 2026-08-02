// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using NPS.Core.Serialization;
using NPS.NIP.Frames;

namespace NPS.NIP.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(IdentFrame))]
[JsonSerializable(typeof(TrustFrame))]
[JsonSerializable(typeof(RevokeFrame))]
[JsonSerializable(typeof(IdentMetadata))]
[JsonSerializable(typeof(IdentReputationPolicyHint))]
[JsonSerializable(typeof(IdentLineage))]
internal partial class NipJsonContext : JsonSerializerContext;

[GeneratedMessagePackResolver]
internal partial class NipGeneratedMessagePackResolver;

internal static class NipMessagePackResolver
{
    private static readonly GenericEnumFormatter<AssuranceLevel> AssuranceLevelFormatter = new();

    internal static IFormatterResolver Instance { get; } = CompositeResolver.Create(
        new IMessagePackFormatter[]
        {
            AssuranceLevelFormatter,
            new StaticNullableFormatter<AssuranceLevel>(AssuranceLevelFormatter),
        },
        new IFormatterResolver[]
        {
            NipGeneratedMessagePackResolver.Instance,
            NpsMessagePackResolver.Instance,
        });
}
