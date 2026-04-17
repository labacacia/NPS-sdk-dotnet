// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.MemoryNode;

/// <summary>
/// Describes a single field in a Memory Node schema (NPS-2 §4.1).
/// </summary>
public sealed class MemoryNodeField
{
    /// <summary>Column / property name as exposed in NWP responses.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// NWP field type: <c>"string"</c>, <c>"number"</c>, <c>"boolean"</c>,
    /// <c>"datetime"</c>, or <c>"object"</c>.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>Human-readable description surfaced in the AnchorFrame schema.</summary>
    public string? Description { get; init; }

    /// <summary>Whether this field may be null. Default <c>true</c>.</summary>
    public bool Nullable { get; init; } = true;

    /// <summary>
    /// Underlying database column name, if different from <see cref="Name"/>.
    /// Defaults to <see cref="Name"/> when null.
    /// </summary>
    public string? ColumnName { get; init; }

    /// <summary>Resolved column name (falls back to <see cref="Name"/>).</summary>
    internal string ResolvedColumnName => ColumnName ?? Name;
}

/// <summary>
/// Schema definition for a Memory Node — describes the DB table it exposes.
/// </summary>
public sealed class MemoryNodeSchema
{
    /// <summary>Database table (or view) name.</summary>
    public required string TableName { get; init; }

    /// <summary>Primary key field name. Used for cursor-based pagination.</summary>
    public required string PrimaryKey { get; init; }

    /// <summary>All queryable fields. Must contain at least the primary key.</summary>
    public required IReadOnlyList<MemoryNodeField> Fields { get; init; }

    /// <summary>Returns the field descriptor for <paramref name="name"/>, or null.</summary>
    public MemoryNodeField? GetField(string name) =>
        Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns true if <paramref name="name"/> is a declared field.</summary>
    public bool HasField(string name) => GetField(name) is not null;
}
