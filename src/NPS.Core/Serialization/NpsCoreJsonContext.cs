// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(AnchorFrame))]
[JsonSerializable(typeof(DiffFrame))]
[JsonSerializable(typeof(StreamFrame))]
[JsonSerializable(typeof(CapsFrame))]
#pragma warning disable CS0618 // AlignFrame retained for backward-compatibility metadata
[JsonSerializable(typeof(AlignFrame))]
#pragma warning restore CS0618
[JsonSerializable(typeof(HelloFrame))]
[JsonSerializable(typeof(ErrorFrame))]
[JsonSerializable(typeof(NcpHandshakeCapsFrame))]
[JsonSerializable(typeof(FrameSchema))]
[JsonSerializable(typeof(SchemaField))]
[JsonSerializable(typeof(JsonPatchOperation))]
internal partial class NpsCoreJsonContext : JsonSerializerContext;
