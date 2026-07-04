// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NPS.NIP.Ca.Ra;

namespace NPS.NIP.Storage;

/// <summary>
/// SQLite-backed RA store for bootstrap tokens and pending enrollment requests
/// (NPS-CR-0005). Raw bootstrap tokens are never persisted.
/// </summary>
public sealed class SqliteNipRaStore : IBootstrapTokenStore, IPendingStore
{
    private readonly string _connectionString;

    private SqliteNipRaStore(string connectionString) =>
        _connectionString = connectionString;

    /// <summary>Opens (or creates) the RA tables and returns a persistent store.</summary>
    public static async Task<SqliteNipRaStore> OpenAsync(
        string connectionString, CancellationToken ct = default)
    {
        var store = new SqliteNipRaStore(connectionString);
        await store.MigrateAsync(ct);
        return store;
    }

    public async Task<string> CreateAsync(string? label, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var raw  = "nps-bootstrap-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var hash = Hash(raw);
        var id   = Guid.NewGuid().ToString("N");

        const string sql = """
            INSERT INTO nip_bootstrap_tokens
                (id, token_hash, label, created_at, expires_at, consumed, revoked)
            VALUES
                (@Id, @Hash, @Label, @CreatedAt, @ExpiresAt, 0, 0)
            """;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Id",        id);
        cmd.Parameters.AddWithValue("@Hash",      hash);
        cmd.Parameters.AddWithValue("@Label",     (object?)label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@ExpiresAt", expiresAt.ToUniversalTime().ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        return raw;
    }

    public async Task<bool> ValidateAndConsumeAsync(string token, CancellationToken ct)
    {
        const string sql = """
            UPDATE nip_bootstrap_tokens
            SET consumed = 1
            WHERE token_hash = @Hash
              AND consumed = 0
              AND revoked = 0
              AND expires_at > @Now
            """;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Hash", Hash(token));
        cmd.Parameters.AddWithValue("@Now",  DateTimeOffset.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<BootstrapTokenInfo>> ListAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id, label, created_at, expires_at, consumed, revoked
            FROM nip_bootstrap_tokens
            ORDER BY created_at DESC
            """;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var results = new List<BootstrapTokenInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new BootstrapTokenInfo(
                reader.GetString(reader.GetOrdinal("id")),
                ReadOptString(reader, "label"),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("expires_at"))),
                reader.GetInt32(reader.GetOrdinal("consumed")) != 0,
                reader.GetInt32(reader.GetOrdinal("revoked")) != 0));
        }
        return results;
    }

    public async Task<bool> RevokeAsync(string tokenId, CancellationToken ct)
    {
        const string sql = """
            UPDATE nip_bootstrap_tokens
            SET revoked = 1
            WHERE id = @Id AND consumed = 0 AND revoked = 0
            """;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Id", tokenId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public int PendingCount
    {
        get
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM nip_pending_requests WHERE status = 'pending'";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public async Task<string> EnqueueAsync(PendingRegistration request, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO nip_pending_requests
                (id, entity_type, identifier, pub_key, capabilities_json, scope_json,
                 metadata_json, requested_at, status, reject_reason)
            VALUES
                (@Id, @EntityType, @Identifier, @PubKey, @CapabilitiesJson, @ScopeJson,
                 @MetadataJson, @RequestedAt, @Status, @RejectReason)
            """;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddPendingParameters(cmd, request);
        await cmd.ExecuteNonQueryAsync(ct);
        return request.Id;
    }

    async Task<IReadOnlyList<PendingRegistration>> IPendingStore.ListAsync(CancellationToken ct)
    {
        const string sql = "SELECT * FROM nip_pending_requests ORDER BY requested_at DESC";
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await ReadPendingListAsync(cmd, ct);
    }

    public async Task<PendingRegistration?> GetAsync(string id, CancellationToken ct)
    {
        const string sql = "SELECT * FROM nip_pending_requests WHERE id = @Id LIMIT 1";
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPending(reader) : null;
    }

    public Task<bool> ApproveAsync(string id, CancellationToken ct) =>
        SetPendingStatusAsync(id, PendingStatus.Approved, null, ct);

    public Task<bool> RejectAsync(string id, string reason, CancellationToken ct) =>
        SetPendingStatusAsync(id, PendingStatus.Rejected, reason, ct);

    private async Task<bool> SetPendingStatusAsync(
        string id, PendingStatus status, string? reason, CancellationToken ct)
    {
        const string sql = """
            UPDATE nip_pending_requests
            SET status = @Status, reject_reason = @Reason
            WHERE id = @Id AND status = 'pending'
            """;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Id",     id);
        cmd.Parameters.AddWithValue("@Status", ToDbStatus(status));
        cmd.Parameters.AddWithValue("@Reason", (object?)reason ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task MigrateAsync(CancellationToken ct)
    {
        string[] statements =
        [
            """
            CREATE TABLE IF NOT EXISTS nip_bootstrap_tokens (
                id         TEXT PRIMARY KEY,
                token_hash TEXT NOT NULL UNIQUE,
                label      TEXT,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed   INTEGER NOT NULL DEFAULT 0,
                revoked    INTEGER NOT NULL DEFAULT 0
            )
            """,
            "CREATE INDEX IF NOT EXISTS idx_nip_bootstrap_tokens_hash ON nip_bootstrap_tokens (token_hash)",
            "CREATE INDEX IF NOT EXISTS idx_nip_bootstrap_tokens_expires ON nip_bootstrap_tokens (expires_at)",
            """
            CREATE TABLE IF NOT EXISTS nip_pending_requests (
                id                TEXT PRIMARY KEY,
                entity_type       TEXT NOT NULL,
                identifier        TEXT NOT NULL,
                pub_key           TEXT NOT NULL,
                capabilities_json TEXT NOT NULL DEFAULT '[]',
                scope_json        TEXT NOT NULL DEFAULT '{}',
                metadata_json     TEXT,
                requested_at      TEXT NOT NULL,
                status            TEXT NOT NULL DEFAULT 'pending',
                reject_reason     TEXT
            )
            """,
            "CREATE INDEX IF NOT EXISTS idx_nip_pending_requests_status ON nip_pending_requests (status, requested_at)",
            "CREATE INDEX IF NOT EXISTS idx_nip_pending_requests_identifier ON nip_pending_requests (identifier)",
        ];

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        foreach (var sql in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static void AddPendingParameters(SqliteCommand cmd, PendingRegistration request)
    {
        cmd.Parameters.AddWithValue("@Id",               request.Id);
        cmd.Parameters.AddWithValue("@EntityType",       request.EntityType);
        cmd.Parameters.AddWithValue("@Identifier",       request.Identifier);
        cmd.Parameters.AddWithValue("@PubKey",           request.PubKey);
        cmd.Parameters.AddWithValue("@CapabilitiesJson", JsonSerializer.Serialize(request.Capabilities));
        cmd.Parameters.AddWithValue("@ScopeJson",        request.ScopeJson);
        cmd.Parameters.AddWithValue("@MetadataJson",     (object?)request.MetadataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@RequestedAt",      request.RequestedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("@Status",           ToDbStatus(request.Status));
        cmd.Parameters.AddWithValue("@RejectReason",     (object?)request.RejectReason ?? DBNull.Value);
    }

    private static async Task<IReadOnlyList<PendingRegistration>> ReadPendingListAsync(
        SqliteCommand cmd, CancellationToken ct)
    {
        var results = new List<PendingRegistration>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadPending(reader));
        return results;
    }

    private static PendingRegistration ReadPending(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("entity_type")),
        reader.GetString(reader.GetOrdinal("identifier")),
        reader.GetString(reader.GetOrdinal("pub_key")),
        JsonSerializer.Deserialize<string[]>(
            reader.GetString(reader.GetOrdinal("capabilities_json"))) ?? [],
        reader.GetString(reader.GetOrdinal("scope_json")),
        ReadOptString(reader, "metadata_json"),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("requested_at"))),
        FromDbStatus(reader.GetString(reader.GetOrdinal("status"))),
        ReadOptString(reader, "reject_reason"));

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string ToDbStatus(PendingStatus status) => status switch
    {
        PendingStatus.Pending  => "pending",
        PendingStatus.Approved => "approved",
        PendingStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static PendingStatus FromDbStatus(string status) => status switch
    {
        "pending"  => PendingStatus.Pending,
        "approved" => PendingStatus.Approved,
        "rejected" => PendingStatus.Rejected,
        _ => throw new InvalidOperationException($"Unknown pending status: {status}"),
    };

    private static string? ReadOptString(SqliteDataReader reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetString(ord);
    }
}
