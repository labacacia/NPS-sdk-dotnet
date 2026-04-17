// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using NPS.NWP.Frames;
using NPS.NWP.MemoryNode.Query;

namespace NPS.NWP.MemoryNode.Providers;

/// <summary>
/// Memory Node provider backed by PostgreSQL (NPS-2 §2.1).
/// Uses Dapper + Npgsql for lightweight dynamic query execution.
/// </summary>
public sealed class PostgreSqlMemoryNodeProvider : IMemoryNodeProvider
{
    private readonly string  _connectionString;
    private readonly ILogger _logger;

    public PostgreSqlMemoryNodeProvider(string connectionString, ILogger<PostgreSqlMemoryNodeProvider> logger)
    {
        _connectionString = connectionString;
        _logger           = logger;
    }

    /// <inheritdoc/>
    public async Task<MemoryNodeQueryResult> QueryAsync(
        QueryFrame        frame,
        MemoryNodeSchema  schema,
        MemoryNodeOptions options,
        CancellationToken ct = default)
    {
        var builder = new SqlQueryBuilder(schema, DatabaseDialect.PostgreSql);
        var (sql, p) = builder.Build(frame, options);

        _logger.LogDebug("PostgreSQL query: {Sql}", sql);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var rawRows = await conn.QueryAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var rows    = MapRows(rawRows);
        var limit   = (long)Math.Min(frame.Limit == 0 ? options.DefaultLimit : frame.Limit, options.MaxLimit);
        var nextCursor = rows.Count == limit
            ? SqlQueryBuilder.EncodeCursor(SqlQueryBuilder.DecodeCursor(frame.Cursor) + limit)
            : null;

        return new MemoryNodeQueryResult { Rows = rows, NextCursor = nextCursor };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StreamAsync(
        QueryFrame        frame,
        MemoryNodeSchema  schema,
        MemoryNodeOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var builder   = new SqlQueryBuilder(schema, DatabaseDialect.PostgreSql);
        var pageLimit = Math.Min(frame.Limit == 0 ? options.DefaultLimit : frame.Limit, options.MaxLimit);
        var cursor    = frame.Cursor;
        bool hasMore  = true;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        while (hasMore && !ct.IsCancellationRequested)
        {
            var pageFrame = frame with { Limit = pageLimit, Cursor = cursor };
            var (sql, p) = builder.Build(pageFrame, options);

            _logger.LogDebug("PostgreSQL stream page: {Sql}", sql);

            var rawRows = await conn.QueryAsync(new CommandDefinition(sql, p, cancellationToken: ct));
            var rows    = MapRows(rawRows);

            if (rows.Count == 0) break;

            yield return rows;

            hasMore = rows.Count == (int)pageLimit;
            cursor  = SqlQueryBuilder.EncodeCursor(SqlQueryBuilder.DecodeCursor(cursor) + rows.Count);
        }
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(
        QueryFrame        frame,
        MemoryNodeSchema  schema,
        CancellationToken ct = default)
    {
        var builder = new SqlQueryBuilder(schema, DatabaseDialect.PostgreSql);
        var (sql, p) = builder.BuildCount(frame);

        _logger.LogDebug("PostgreSQL count: {Sql}", sql);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, p, cancellationToken: ct));
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> MapRows(IEnumerable<dynamic> rawRows)
    {
        var result = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var row in rawRows)
        {
            var dict  = (IDictionary<string, object>)row;
            var typed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in dict)
                typed[k] = v;
            result.Add(typed);
        }
        return result;
    }
}
