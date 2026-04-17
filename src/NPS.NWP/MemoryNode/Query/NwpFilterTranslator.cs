// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Dapper;
using NPS.NWP.Http;

namespace NPS.NWP.MemoryNode.Query;

/// <summary>
/// Translates a NWP filter predicate (NPS-2 §5.2) to a parameterized SQL WHERE clause.
/// Validates all field names against the schema to prevent SQL injection.
/// </summary>
public sealed class NwpFilterTranslator
{
    private readonly MemoryNodeSchema _schema;
    private readonly string           _quote;   // "[" for SQL Server, "\"" for PG
    private int _paramIndex;

    public NwpFilterTranslator(MemoryNodeSchema schema, DatabaseDialect dialect)
    {
        _schema = schema;
        _quote  = dialect == DatabaseDialect.SqlServer ? "[" : "\"";
    }

    /// <summary>
    /// Translates <paramref name="filter"/> into a WHERE clause fragment and populates
    /// <paramref name="parameters"/> with the corresponding Dapper parameters.
    /// Returns an empty string when <paramref name="filter"/> is null.
    /// </summary>
    /// <exception cref="NwpFilterException">Thrown on unknown field or unsupported operator.</exception>
    public string Translate(JsonElement? filter, DynamicParameters parameters)
    {
        _paramIndex = 0;
        if (filter is null || filter.Value.ValueKind == JsonValueKind.Null)
            return string.Empty;

        return BuildObject(filter.Value, parameters);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private string BuildObject(JsonElement obj, DynamicParameters p)
    {
        var clauses = new List<string>();

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.StartsWith('$'))
            {
                clauses.Add(BuildLogical(prop.Name, prop.Value, p));
            }
            else
            {
                var field = ValidateField(prop.Name);
                clauses.Add(BuildFieldCondition(field, prop.Value, p));
            }
        }

        return clauses.Count switch
        {
            0 => string.Empty,
            1 => clauses[0],
            _ => $"({string.Join(" AND ", clauses)})"
        };
    }

    private string BuildLogical(string op, JsonElement value, DynamicParameters p)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new NwpFilterException($"Logical operator '{op}' requires an array value.");

        var separator = op switch
        {
            "$and" => " AND ",
            "$or"  => " OR ",
            _ => throw new NwpFilterException($"Unknown logical operator '{op}'.")
        };

        var parts = value.EnumerateArray()
            .Select(el => BuildObject(el, p))
            .Where(s => s.Length > 0)
            .ToList();

        return parts.Count switch
        {
            0 => string.Empty,
            1 => parts[0],
            _ => $"({string.Join(separator, parts)})"
        };
    }

    private string BuildFieldCondition(MemoryNodeField field, JsonElement condition, DynamicParameters p)
    {
        if (condition.ValueKind != JsonValueKind.Object)
            throw new NwpFilterException($"Field '{field.Name}' condition must be an object (e.g. {{\"$eq\": value}}).");

        var col   = QuoteColumn(field.ResolvedColumnName);
        var parts = new List<string>();

        foreach (var prop in condition.EnumerateObject())
        {
            // $in / $nin / $between allocate their own parameter names internally.
            // Simple comparison ops allocate a single parameter here.
            parts.Add(prop.Name switch
            {
                "$in"      => BuildIn(col, prop.Value, p, negate: false),
                "$nin"     => BuildIn(col, prop.Value, p, negate: true),
                "$between" => BuildBetween(col, prop.Value, p),
                var op     => BuildSimple(col, op, field.Name, prop.Value, p),
            });
        }

        return parts.Count == 1 ? parts[0] : $"({string.Join(" AND ", parts)})";
    }

    private string BuildSimple(string col, string op, string fieldName, JsonElement value, DynamicParameters p)
    {
        var paramName = $"p{_paramIndex++}";
        return op switch
        {
            "$eq"       => $"{col} = @{paramName}"      .Tap(() => p.Add(paramName, ExtractValue(value))),
            "$ne"       => $"{col} <> @{paramName}"     .Tap(() => p.Add(paramName, ExtractValue(value))),
            "$lt"       => $"{col} < @{paramName}"      .Tap(() => p.Add(paramName, ExtractValue(value))),
            "$lte"      => $"{col} <= @{paramName}"     .Tap(() => p.Add(paramName, ExtractValue(value))),
            "$gt"       => $"{col} > @{paramName}"      .Tap(() => p.Add(paramName, ExtractValue(value))),
            "$gte"      => $"{col} >= @{paramName}"     .Tap(() => p.Add(paramName, ExtractValue(value))),
            "$contains" => $"{col} LIKE @{paramName}"   .Tap(() => p.Add(paramName, $"%{ExtractValue(value)}%")),
            _ => throw new NwpFilterException($"Unknown filter operator '{op}' on field '{fieldName}'.")
        };
    }

    private string BuildIn(string col, JsonElement arr, DynamicParameters p, bool negate)
    {
        if (arr.ValueKind != JsonValueKind.Array)
            throw new NwpFilterException($"$in/$nin requires an array value.");

        var values = arr.EnumerateArray().Select(ExtractValue).ToList();
        if (values.Count == 0)
            return negate ? "1=1" : "1=0";   // empty IN → always false; empty NIN → always true

        var paramName = $"p{_paramIndex++}";
        p.Add(paramName, values);
        return negate ? $"{col} NOT IN @{paramName}" : $"{col} IN @{paramName}";
    }

    private string BuildBetween(string col, JsonElement arr, DynamicParameters p)
    {
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() != 2)
            throw new NwpFilterException("$between requires an array of exactly two values [low, high].");

        var items   = arr.EnumerateArray().ToList();
        var pLow    = $"p{_paramIndex++}";
        var pHigh   = $"p{_paramIndex++}";
        p.Add(pLow,  ExtractValue(items[0]));
        p.Add(pHigh, ExtractValue(items[1]));
        return $"{col} BETWEEN @{pLow} AND @{pHigh}";
    }

    private MemoryNodeField ValidateField(string name)
    {
        var field = _schema.GetField(name)
            ?? throw new NwpFilterException($"Unknown field '{name}'.", NwpErrorCodes.QueryFieldUnknown);
        return field;
    }

    private string QuoteColumn(string col) =>
        _quote == "[" ? $"[{col}]" : $"\"{col}\"";

    private static object? ExtractValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String  => el.GetString(),
        JsonValueKind.Number  => el.TryGetInt64(out var l) ? (object?)l : el.GetDouble(),
        JsonValueKind.True    => true,
        JsonValueKind.False   => false,
        JsonValueKind.Null    => null,
        _ => el.GetRawText()
    };
}

/// <summary>Supported SQL dialects for quoting and pagination syntax.</summary>
public enum DatabaseDialect { SqlServer, PostgreSql }

/// <summary>Thrown when a NWP filter cannot be translated to SQL.</summary>
public sealed class NwpFilterException : Exception
{
    public string NwpErrorCode { get; }

    public NwpFilterException(string message, string errorCode = NwpErrorCodes.QueryFilterInvalid)
        : base(message) => NwpErrorCode = errorCode;
}

/// <summary>Fluent tap helper — executes a side-effect and returns the string unchanged.</summary>
file static class StringTapExtensions
{
    public static string Tap(this string s, Action action) { action(); return s; }
}
