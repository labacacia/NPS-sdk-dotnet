// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using NPS.Core.Serialization;
using NPS.NDP.Frames;

namespace NPS.NDP.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(AnnounceFrame))]
[JsonSerializable(typeof(ResolveFrame))]
[JsonSerializable(typeof(GraphFrame))]
[JsonSerializable(typeof(NdpAddress))]
[JsonSerializable(typeof(NdpResolveResult))]
[JsonSerializable(typeof(NdpGraphNode))]
[JsonSerializable(typeof(NdpGraphEdge))]
internal partial class NdpJsonContext : JsonSerializerContext;

[GeneratedMessagePackResolver]
internal partial class NdpGeneratedMessagePackResolver;

internal static class NdpMessagePackResolver
{
    internal static IFormatterResolver Instance { get; } = CompositeResolver.Create(
        new IMessagePackFormatter[]
        {
            Int32Formatter.Instance,
            new InterfaceReadOnlyListFormatter<NdpAddress>(),
            new InterfaceReadOnlyListFormatter<NdpGraphNode>(),
            new InterfaceReadOnlyListFormatter<NdpGraphEdge>(),
        },
        new IFormatterResolver[]
        {
            NdpGeneratedMessagePackResolver.Instance,
            NpsMessagePackResolver.Instance,
        });
}
