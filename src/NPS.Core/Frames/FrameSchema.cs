// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace NPS.Core.Frames;

/// <summary>
/// Schema definition carried inside an <see cref="Ncp.AnchorFrame"/>.
/// Defines field names, primitive types, and optional semantic annotations.
/// </summary>
public sealed record FrameSchema
{
    /// <summary>Ordered list of field descriptors.</summary>
    public required IReadOnlyList<SchemaField> Fields { get; init; }
}

/// <summary>
/// Descriptor for a single field within a <see cref="FrameSchema"/>.
/// </summary>
/// <param name="Name">Field name as it appears in serialised data.</param>
/// <param name="Type">
/// Primitive type string: <c>uint64</c>, <c>int32</c>, <c>string</c>,
/// <c>decimal</c>, <c>bool</c>, <c>timestamp</c>, <c>bytes</c>.
/// </param>
/// <param name="Semantic">
/// Optional dot-notation semantic annotation understood by AI models,
/// e.g. <c>entity.id</c>, <c>entity.label</c>, <c>commerce.price.usd</c>.
/// </param>
/// <param name="Nullable">
/// Whether the field may be null. Defaults to <c>false</c> (NPS-1 §4.1).
/// </param>
public sealed record SchemaField(
    [property: JsonPropertyName("name")]     string  Name,
    [property: JsonPropertyName("type")]     string  Type,
    [property: JsonPropertyName("semantic")] string? Semantic = null,
    [property: JsonPropertyName("nullable")] bool    Nullable = false);
