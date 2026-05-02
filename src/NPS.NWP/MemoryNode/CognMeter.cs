// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;

namespace NPS.NWP.MemoryNode;

/// <summary>
/// Measures Cognon (CGN) cost of a serialized payload.
/// Default unit: ceil(UTF-8_bytes / 4) — the UTF-8/4 fallback defined in token-budget.md.
/// </summary>
public static class CognMeter
{
    /// <summary>
    /// Returns the CGN cost for a raw UTF-8 byte sequence.
    /// Formula: ceil(byteCount / 4).
    /// </summary>
    public static uint Measure(ReadOnlySpan<byte> utf8Bytes) =>
        (uint)((utf8Bytes.Length + 3) / 4);

    /// <summary>
    /// Returns the CGN cost for a string (encodes to UTF-8 first).
    /// Formula: ceil(UTF-8_bytes / 4).
    /// </summary>
    public static uint Measure(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var byteCount = Encoding.UTF8.GetByteCount(text);
        return (uint)((byteCount + 3) / 4);
    }

    /// <summary>
    /// Serializes <paramref name="value"/> to JSON and measures the CGN cost.
    /// </summary>
    public static uint MeasureJson<T>(T value, JsonSerializerOptions? options = null)
    {
        var json = JsonSerializer.Serialize(value, options);
        return Measure(json);
    }

    /// <summary>
    /// Measures the CGN cost of a collection of row dictionaries (rows serialized as JSON).
    /// </summary>
    public static uint MeasureRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        JsonSerializerOptions? options = null)
    {
        uint total = 0;
        foreach (var row in rows)
            total += MeasureJson(row, options);
        return total;
    }
}
