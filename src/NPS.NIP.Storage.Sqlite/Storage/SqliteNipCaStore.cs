// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NPS.NIP.Ca;

namespace NPS.NIP.Storage;

/// <summary>
/// SQLite-backed NIP CA certificate store (NPS-3 §8).
/// Suitable for single-binary / embedded CA deployments.
/// Use <see cref="OpenAsync"/> to create and migrate the database before registering with DI.
/// </summary>
public sealed class SqliteNipCaStore : INipCaStore
{
    private readonly string _connectionString;

    private SqliteNipCaStore(string connectionString) =>
        _connectionString = connectionString;

    /// <summary>
    /// Opens (or creates) a SQLite database at <paramref name="connectionString"/>
    /// and applies the NIP CA schema migrations.
    /// </summary>
    public static async Task<SqliteNipCaStore> OpenAsync(
        string connectionString, CancellationToken ct = default)
    {
        var store = new SqliteNipCaStore(connectionString);
        await store.MigrateAsync(ct);
        return store;
    }

    // ── INipCaStore ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task SaveAsync(NipCertRecord record, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO nip_certs
                (nid, entity_type, serial, pub_key, capabilities_json, scope_json,
                 issued_by, issued_at, expires_at, metadata_json,
                 nid_role, parent_nid, lineage_json)
            VALUES
                (@Nid, @EntityType, @Serial, @PubKey, @CapJson, @ScopeJson,
                 @IssuedBy, @IssuedAt, @ExpiresAt, @MetaJson,
                 @NidRole, @ParentNid, @LineageJson)
            """;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Nid",         record.Nid);
        cmd.Parameters.AddWithValue("@EntityType",  record.EntityType);
        cmd.Parameters.AddWithValue("@Serial",      record.Serial);
        cmd.Parameters.AddWithValue("@PubKey",      record.PubKey);
        cmd.Parameters.AddWithValue("@CapJson",     JsonSerializer.Serialize(record.Capabilities));
        cmd.Parameters.AddWithValue("@ScopeJson",   record.ScopeJson);
        cmd.Parameters.AddWithValue("@IssuedBy",    record.IssuedBy);
        cmd.Parameters.AddWithValue("@IssuedAt",    record.IssuedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@ExpiresAt",   record.ExpiresAt.ToString("O"));
        cmd.Parameters.AddWithValue("@MetaJson",    (object?)record.MetadataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NidRole",     (object?)record.NidRole      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ParentNid",   (object?)record.ParentNid    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LineageJson", (object?)record.LineageJson  ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<NipCertRecord?> GetByNidAsync(string nid, CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM nip_certs
            WHERE nid = @Nid
            ORDER BY issued_at DESC
            LIMIT 1
            """;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await QueryFirstOrDefaultAsync(conn,
            sql, cmd => cmd.Parameters.AddWithValue("@Nid", nid), ct);
    }

    /// <inheritdoc/>
    public async Task<NipCertRecord?> GetBySerialAsync(string serial, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM nip_certs WHERE serial = @Serial LIMIT 1";
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await QueryFirstOrDefaultAsync(conn,
            sql, cmd => cmd.Parameters.AddWithValue("@Serial", serial), ct);
    }

    /// <inheritdoc/>
    public async Task<bool> RevokeAsync(string nid, string reason, DateTime revokedAt,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE nip_certs
            SET revoked_at = @RevokedAt, revoke_reason = @Reason
            WHERE nid = @Nid AND revoked_at IS NULL
            """;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Nid",       nid);
        cmd.Parameters.AddWithValue("@Reason",    reason);
        cmd.Parameters.AddWithValue("@RevokedAt", revokedAt.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <inheritdoc/>
    public async Task<string> NextSerialAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        // SQLite file-level locking makes this UPDATE + SELECT atomic per connection
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var upd = conn.CreateCommand();
        upd.Transaction = (SqliteTransaction)tx;
        upd.CommandText = "UPDATE nip_serial SET seq = seq + 1 WHERE id = 1";
        await upd.ExecuteNonQueryAsync(ct);

        await using var sel = conn.CreateCommand();
        sel.Transaction = (SqliteTransaction)tx;
        sel.CommandText = "SELECT seq FROM nip_serial WHERE id = 1";
        var next = Convert.ToInt64(await sel.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        await tx.CommitAsync(ct);
        return $"0x{next:X}";
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NipCertRecord>> ListAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM nip_certs ORDER BY issued_at DESC";
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var results = new List<NipCertRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRecord(reader));
        return results;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NipCertRecord>> GetRevokedAsync(CancellationToken ct = default)
    {
        const string sql =
            "SELECT * FROM nip_certs WHERE revoked_at IS NOT NULL ORDER BY revoked_at DESC";
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var results = new List<NipCertRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRecord(reader));
        return results;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NipCertRecord>> GetByParentNidAsync(
        string parentNid, CancellationToken ct = default)
    {
        const string sql =
            "SELECT * FROM nip_certs WHERE parent_nid = @ParentNid ORDER BY issued_at DESC";
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@ParentNid", parentNid);
        var results = new List<NipCertRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRecord(reader));
        return results;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task MigrateAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Run each DDL statement individually; SQLite command executes one statement at a time.
        // The `nid_role` / `parent_nid` / `lineage_json` columns (NPS-CR-0003)
        // are added in a fresh CREATE for new databases and via ALTER TABLE
        // for upgrades — SQLite does not support `ADD COLUMN IF NOT EXISTS`,
        // so we wrap each ALTER in try/catch via PRAGMA table_info inspection.
        string[] statements =
        [
            """
            CREATE TABLE IF NOT EXISTS nip_certs (
                nid               TEXT NOT NULL,
                entity_type       TEXT NOT NULL,
                serial            TEXT NOT NULL UNIQUE,
                pub_key           TEXT NOT NULL,
                capabilities_json TEXT NOT NULL DEFAULT '[]',
                scope_json        TEXT NOT NULL DEFAULT '{}',
                issued_by         TEXT NOT NULL,
                issued_at         TEXT NOT NULL,
                expires_at        TEXT NOT NULL,
                revoked_at        TEXT,
                revoke_reason     TEXT,
                metadata_json     TEXT,
                nid_role          TEXT,
                parent_nid        TEXT,
                lineage_json      TEXT
            )
            """,
            "CREATE INDEX IF NOT EXISTS idx_nip_certs_nid        ON nip_certs (nid)",
            "CREATE INDEX IF NOT EXISTS idx_nip_certs_serial     ON nip_certs (serial)",
            "CREATE INDEX IF NOT EXISTS idx_nip_certs_parent_nid ON nip_certs (parent_nid)",
            """
            CREATE TABLE IF NOT EXISTS nip_serial (
                id   INTEGER PRIMARY KEY,
                seq  INTEGER NOT NULL DEFAULT 0
            )
            """,
            "INSERT OR IGNORE INTO nip_serial (id, seq) VALUES (1, 0)",
        ];

        foreach (var sql in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Backfill the NPS-CR-0003 columns on databases created prior to this
        // migration. SQLite lacks IF NOT EXISTS on ADD COLUMN, so we discover
        // existing columns via PRAGMA and add only the missing ones.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(nip_certs)";
            await using var reader = await pragma.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                existing.Add(reader.GetString(reader.GetOrdinal("name")));
        }
        string[] cr0003Columns =
        [
            "nid_role     TEXT",
            "parent_nid   TEXT",
            "lineage_json TEXT",
        ];
        foreach (var col in cr0003Columns)
        {
            var name = col.Split(' ', 2)[0];
            if (existing.Contains(name)) continue;
            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE nip_certs ADD COLUMN {col}";
            await alter.ExecuteNonQueryAsync(ct);
        }
        await using (var idx = conn.CreateCommand())
        {
            idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_nip_certs_parent_nid ON nip_certs (parent_nid)";
            await idx.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<NipCertRecord?> QueryFirstOrDefaultAsync(
        SqliteConnection conn, string sql,
        Action<SqliteCommand> paramSetup, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        paramSetup(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRecord(reader);
    }

    private static NipCertRecord ReadRecord(SqliteDataReader r) => new()
    {
        Nid          = r.GetString(r.GetOrdinal("nid")),
        EntityType   = r.GetString(r.GetOrdinal("entity_type")),
        Serial       = r.GetString(r.GetOrdinal("serial")),
        PubKey       = r.GetString(r.GetOrdinal("pub_key")),
        Capabilities = JsonSerializer.Deserialize<string[]>(
                           r.GetString(r.GetOrdinal("capabilities_json"))) ?? [],
        ScopeJson    = r.GetString(r.GetOrdinal("scope_json")),
        IssuedBy     = r.GetString(r.GetOrdinal("issued_by")),
        IssuedAt     = DateTime.Parse(
                           r.GetString(r.GetOrdinal("issued_at")),
                           null, DateTimeStyles.RoundtripKind),
        ExpiresAt    = DateTime.Parse(
                           r.GetString(r.GetOrdinal("expires_at")),
                           null, DateTimeStyles.RoundtripKind),
        RevokedAt    = r.IsDBNull(r.GetOrdinal("revoked_at")) ? null :
                       DateTime.Parse(
                           r.GetString(r.GetOrdinal("revoked_at")),
                           null, DateTimeStyles.RoundtripKind),
        RevokeReason = r.IsDBNull(r.GetOrdinal("revoke_reason")) ? null :
                       r.GetString(r.GetOrdinal("revoke_reason")),
        MetadataJson = r.IsDBNull(r.GetOrdinal("metadata_json")) ? null :
                       r.GetString(r.GetOrdinal("metadata_json")),
        NidRole      = ReadOptString(r, "nid_role"),
        ParentNid    = ReadOptString(r, "parent_nid"),
        LineageJson  = ReadOptString(r, "lineage_json"),
    };

    private static string? ReadOptString(SqliteDataReader r, string column)
    {
        // Tolerate older databases that have not yet been migrated — when
        // the column is absent, GetOrdinal throws IndexOutOfRangeException.
        // The migration adds these columns at OpenAsync time, so this is
        // defensive (covers external connections that bypass MigrateAsync).
        try
        {
            var ord = r.GetOrdinal(column);
            return r.IsDBNull(ord) ? null : r.GetString(ord);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }
}
