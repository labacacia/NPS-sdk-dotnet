// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NWP.Frames;

namespace NPS.NWP.MemoryNode;

/// <summary>
/// Result of a Memory Node query (NPS-2 §5).
/// </summary>
public sealed class MemoryNodeQueryResult
{
    /// <summary>Rows returned by the query, each as a field-name → value dictionary.</summary>
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }

    /// <summary>Opaque Base64-URL cursor for the next page. <c>null</c> = last page.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total number of rows in this result (= <c>Rows.Count</c>).</summary>
    public int Count => Rows.Count;
}

/// <summary>
/// Abstraction over a relational database backing a Memory Node (NPS-2 §2.1).
/// Implement this interface for each supported database engine.
/// </summary>
public interface IMemoryNodeProvider
{
    /// <summary>
    /// Executes a query and returns a paginated result.
    /// </summary>
    /// <param name="frame">Decoded <see cref="QueryFrame"/> from the Agent.</param>
    /// <param name="schema">Schema for this node, used for field and filter validation.</param>
    /// <param name="options">Runtime options (limits, auth, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MemoryNodeQueryResult> QueryAsync(
        QueryFrame          frame,
        MemoryNodeSchema    schema,
        MemoryNodeOptions   options,
        CancellationToken   ct = default);

    /// <summary>
    /// Streams all matching rows as an async sequence of pages (for <c>/stream</c> endpoint).
    /// Each yielded list represents one StreamFrame's data chunk.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StreamAsync(
        QueryFrame          frame,
        MemoryNodeSchema    schema,
        MemoryNodeOptions   options,
        CancellationToken   ct = default);

    /// <summary>
    /// Returns the total row count matching the frame's filter.
    /// Used for cursor validation and pagination metadata.
    /// </summary>
    Task<long> CountAsync(
        QueryFrame          frame,
        MemoryNodeSchema    schema,
        CancellationToken   ct = default);
}
