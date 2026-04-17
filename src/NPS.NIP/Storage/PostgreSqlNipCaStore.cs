// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Dapper;
using Npgsql;
using NPS.NIP.Ca;

namespace NPS.NIP.Storage;

/// <summary>
/// PostgreSQL-backed NIP CA certificate store (NPS-3 §8).
/// Requires the schema created by <c>db/001_init.sql</c>.
/// Uses Dapper + Npgsql for lightweight access.
/// </summary>
public sealed class PostgreSqlNipCaStore : INipCaStore
{
    private readonly string _connectionString;

    public PostgreSqlNipCaStore(string connectionString) =>
        _connectionString = connectionString;

    // ── INipCaStore ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task SaveAsync(NipCertRecord record, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO nip_certificates
                (nid, entity_type, serial, pub_key, capabilities, scope_json,
                 issued_by, issued_at, expires_at, metadata_json)
            VALUES
                (@Nid, @EntityType, @Serial, @PubKey, @Capabilities, @ScopeJson::jsonb,
                 @IssuedBy, @IssuedAt, @ExpiresAt, @MetadataJson::jsonb)
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            record.Nid,
            record.EntityType,
            record.Serial,
            record.PubKey,
            Capabilities = record.Capabilities,   // Npgsql maps string[] → text[]
            record.ScopeJson,
            record.IssuedBy,
            record.IssuedAt,
            record.ExpiresAt,
            MetadataJson = record.MetadataJson ?? "null",
        }, cancellationToken: ct));
    }

    /// <inheritdoc/>
    public async Task<NipCertRecord?> GetByNidAsync(string nid, CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM nip_certificates
            WHERE nid = @Nid
            ORDER BY issued_at DESC
            LIMIT 1
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<CertRow>(
            new CommandDefinition(sql, new { Nid = nid }, cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    /// <inheritdoc/>
    public async Task<NipCertRecord?> GetBySerialAsync(string serial, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM nip_certificates WHERE serial = @Serial LIMIT 1";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<CertRow>(
            new CommandDefinition(sql, new { Serial = serial }, cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    /// <inheritdoc/>
    public async Task<bool> RevokeAsync(string nid, string reason, DateTime revokedAt,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE nip_certificates
            SET revoked_at = @RevokedAt, revoke_reason = @Reason
            WHERE nid = @Nid AND revoked_at IS NULL
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql,
            new { Nid = nid, Reason = reason, RevokedAt = revokedAt }, cancellationToken: ct));
        return rows > 0;
    }

    /// <inheritdoc/>
    public async Task<string> NextSerialAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT nextval('nip_serial_seq')";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var next = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, cancellationToken: ct));
        return $"0x{next:X}";
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NipCertRecord>> GetRevokedAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM nip_certificates WHERE revoked_at IS NOT NULL ORDER BY revoked_at DESC";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<CertRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    // Dapper row model (snake_case column names)
    private sealed class CertRow
    {
        public string   Nid           { get; set; } = "";
        public string   Entity_type   { get; set; } = "";
        public string   Serial        { get; set; } = "";
        public string   Pub_key       { get; set; } = "";
        public string[] Capabilities  { get; set; } = [];
        public string   Scope_json    { get; set; } = "{}";
        public string   Issued_by     { get; set; } = "";
        public DateTime Issued_at     { get; set; }
        public DateTime Expires_at    { get; set; }
        public DateTime? Revoked_at   { get; set; }
        public string?  Revoke_reason { get; set; }
        public string?  Metadata_json { get; set; }
    }

    private static NipCertRecord MapRow(CertRow r) => new()
    {
        Nid          = r.Nid,
        EntityType   = r.Entity_type,
        Serial       = r.Serial,
        PubKey       = r.Pub_key,
        Capabilities = r.Capabilities,
        ScopeJson    = r.Scope_json,
        IssuedBy     = r.Issued_by,
        IssuedAt     = DateTime.SpecifyKind(r.Issued_at,  DateTimeKind.Utc),
        ExpiresAt    = DateTime.SpecifyKind(r.Expires_at, DateTimeKind.Utc),
        RevokedAt    = r.Revoked_at.HasValue
            ? DateTime.SpecifyKind(r.Revoked_at.Value, DateTimeKind.Utc)
            : null,
        RevokeReason = r.Revoke_reason,
        MetadataJson = r.Metadata_json,
    };
}
