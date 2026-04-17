// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Dapper;
using NPS.NWP.Frames;
using NPS.NWP.Http;

namespace NPS.NWP.MemoryNode.Query;

/// <summary>
/// Builds a complete parameterized SELECT query from a <see cref="QueryFrame"/>,
/// handling field projection, filter, ordering, and cursor-based pagination.
/// Dialect-specific quoting and LIMIT syntax is injected via <see cref="DatabaseDialect"/>.
/// </summary>
public sealed class SqlQueryBuilder
{
    private readonly MemoryNodeSchema    _schema;
    private readonly DatabaseDialect     _dialect;
    private readonly NwpFilterTranslator _filter;

    public SqlQueryBuilder(MemoryNodeSchema schema, DatabaseDialect dialect)
    {
        _schema  = schema;
        _dialect = dialect;
        _filter  = new NwpFilterTranslator(schema, dialect);
    }

    /// <summary>Builds the full SELECT query and its parameters.</summary>
    public (string Sql, DynamicParameters Params) Build(
        QueryFrame        frame,
        MemoryNodeOptions options)
    {
        var p      = new DynamicParameters();
        var sb     = new StringBuilder();
        var limit  = Math.Min(frame.Limit == 0 ? options.DefaultLimit : frame.Limit, options.MaxLimit);
        var offset = DecodeCursor(frame.Cursor);

        // SELECT
        sb.Append("SELECT ").Append(BuildSelectList(frame.Fields));

        // FROM
        sb.Append(" FROM ").Append(QuoteTable(_schema.TableName));

        // WHERE
        var where = _filter.Translate(frame.Filter, p);
        if (!string.IsNullOrEmpty(where))
            sb.Append(" WHERE ").Append(where);

        // ORDER BY (required for stable pagination)
        if (frame.Order is { Count: > 0 })
        {
            sb.Append(" ORDER BY ").Append(BuildOrderBy(frame.Order));
        }
        else
        {
            sb.Append(" ORDER BY ").Append(QuoteColumn(_schema.PrimaryKey));
        }

        // PAGINATION — dialect-specific syntax
        if (_dialect == DatabaseDialect.SqlServer)
        {
            sb.Append(" OFFSET @_offset ROWS FETCH NEXT @_limit ROWS ONLY");
        }
        else
        {
            sb.Append(" LIMIT @_limit OFFSET @_offset");
        }

        p.Add("_limit",  (int)limit);
        p.Add("_offset", (int)offset);

        return (sb.ToString(), p);
    }

    /// <summary>Builds a COUNT(*) query for the same filter (used for cursor validation).</summary>
    public (string Sql, DynamicParameters Params) BuildCount(QueryFrame frame)
    {
        var p  = new DynamicParameters();
        var sb = new StringBuilder();

        sb.Append("SELECT COUNT(*) FROM ").Append(QuoteTable(_schema.TableName));

        var where = _filter.Translate(frame.Filter, p);
        if (!string.IsNullOrEmpty(where))
            sb.Append(" WHERE ").Append(where);

        return (sb.ToString(), p);
    }

    // ── Cursor ────────────────────────────────────────────────────────────────

    /// <summary>Encodes a row offset as an opaque Base64-URL cursor.</summary>
    public static string? EncodeCursor(long nextOffset) =>
        nextOffset <= 0 ? null
        : Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"o\":{nextOffset}}}")
            ).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes a Base64-URL cursor back to a row offset. Returns 0 for null/invalid.</summary>
    public static long DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return 0;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("o", out var el) ? el.GetInt64() : 0;
        }
        catch { return 0; }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private string BuildSelectList(IReadOnlyList<string>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            // Return all declared schema fields (not SELECT *, to avoid schema drift)
            return string.Join(", ", _schema.Fields.Select(f => QuoteColumn(f.ResolvedColumnName)));
        }

        foreach (var name in fields)
        {
            if (!_schema.HasField(name))
                throw new NwpFilterException($"Unknown field '{name}'.", NwpErrorCodes.QueryFieldUnknown);
        }

        return string.Join(", ", fields.Select(name =>
        {
            var f = _schema.GetField(name)!;
            var col = QuoteColumn(f.ResolvedColumnName);
            // Alias back to the NWP name if column name differs
            return f.ColumnName is not null ? $"{col} AS {QuoteColumn(f.Name)}" : col;
        }));
    }

    private string BuildOrderBy(IReadOnlyList<QueryOrderClause> order)
    {
        return string.Join(", ", order.Select(o =>
        {
            var field = _schema.GetField(o.Field)
                ?? throw new NwpFilterException($"Unknown order field '{o.Field}'.", NwpErrorCodes.QueryFieldUnknown);
            var dir = o.Dir.ToUpperInvariant() == "DESC" ? "DESC" : "ASC";
            return $"{QuoteColumn(field.ResolvedColumnName)} {dir}";
        }));
    }

    private string QuoteColumn(string col) =>
        _dialect == DatabaseDialect.SqlServer ? $"[{col}]" : $"\"{col}\"";

    private string QuoteTable(string table) =>
        _dialect == DatabaseDialect.SqlServer ? $"[{table}]" : $"\"{table}\"";
}
