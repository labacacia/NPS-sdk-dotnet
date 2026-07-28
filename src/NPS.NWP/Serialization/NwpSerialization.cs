// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using NPS.Core.Serialization;
using NPS.NWP.Frames;

namespace NPS.NWP.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(QueryFrame))]
[JsonSerializable(typeof(ActionFrame))]
[JsonSerializable(typeof(SubscribeFrame))]
[JsonSerializable(typeof(QueryOrderClause))]
[JsonSerializable(typeof(VectorSearchOptions))]
internal partial class NwpJsonContext : JsonSerializerContext;

[GeneratedMessagePackResolver]
internal partial class NwpGeneratedMessagePackResolver;

internal static class NwpMessagePackResolver
{
    internal static IFormatterResolver Instance { get; } = CompositeResolver.Create(
        new IMessagePackFormatter[]
        {
            NullableDoubleFormatter.Instance,
            SingleArrayFormatter.Instance,
            new InterfaceReadOnlyListFormatter<QueryOrderClause>(),
        },
        new IFormatterResolver[]
        {
            NwpGeneratedMessagePackResolver.Instance,
            NpsMessagePackResolver.Instance,
        });
}
