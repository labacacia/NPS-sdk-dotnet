// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using NPS.NIP.Ca.Ra;

namespace NPS.NIP.Storage;

/// <summary>
/// PostgreSQL-backed RA store for bootstrap tokens and pending enrollment
/// requests (NPS-CR-0005). Raw bootstrap tokens are never persisted.
/// </summary>
public sealed class PostgreSqlNipRaStore : IBootstrapTokenStore, IPendingStore
{
    private readonly string _connectionString;

    public PostgreSqlNipRaStore(string connectionString) =>
        _connectionString = connectionString;

    /// <summary>Creates or upgrades the RA tables used by this store.</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        const string sql = """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'nip_bootstrap_tokens'
                      AND column_name IN ('token_id', 'hashed_token', 'used')
                ) THEN
                    ALTER TABLE nip_bootstrap_tokens
                        RENAME TO nip_bootstrap_tokens_legacy_alpha15;
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'nip_pending_requests'
                      AND column_name IN ('pending_id', 'nid', 'submitted_at')
                ) THEN
                    ALTER TABLE nip_pending_requests
                        RENAME TO nip_pending_requests_legacy_alpha15;
                END IF;
            END $$;

            CREATE TABLE IF NOT EXISTS nip_bootstrap_tokens (
                id          TEXT        PRIMARY KEY,
                token_hash  TEXT        NOT NULL UNIQUE,
                label       TEXT,
                created_at  TIMESTAMPTZ NOT NULL,
                expires_at  TIMESTAMPTZ NOT NULL,
                consumed    BOOLEAN     NOT NULL DEFAULT false,
                revoked     BOOLEAN     NOT NULL DEFAULT false
            );
            CREATE INDEX IF NOT EXISTS idx_nip_bootstrap_tokens_hash
                ON nip_bootstrap_tokens (token_hash);
            CREATE INDEX IF NOT EXISTS idx_nip_bootstrap_tokens_expires
                ON nip_bootstrap_tokens (expires_at);

            CREATE TABLE IF NOT EXISTS nip_pending_requests (
                id           TEXT        PRIMARY KEY,
                entity_type  TEXT        NOT NULL,
                identifier   TEXT        NOT NULL,
                pub_key      TEXT        NOT NULL,
                capabilities TEXT[]      NOT NULL DEFAULT '{}',
                scope_json   JSONB       NOT NULL DEFAULT '{}',
                metadata_json JSONB,
                requested_at TIMESTAMPTZ NOT NULL,
                status       TEXT        NOT NULL DEFAULT 'pending'
                    CHECK (status IN ('pending', 'approved', 'rejected')),
                reject_reason TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_nip_pending_requests_status
                ON nip_pending_requests (status, requested_at);
            CREATE INDEX IF NOT EXISTS idx_nip_pending_requests_identifier
                ON nip_pending_requests (identifier);
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<string> CreateAsync(string? label, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var raw  = "nps-bootstrap-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var id   = Guid.NewGuid().ToString("N");
        var hash = Hash(raw);

        const string sql = """
            INSERT INTO nip_bootstrap_tokens
                (id, token_hash, label, created_at, expires_at, consumed, revoked)
            VALUES
                (@Id, @Hash, @Label, @CreatedAt, @ExpiresAt, false, false)
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id        = id,
            Hash      = hash,
            Label     = label,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt.ToUniversalTime(),
        }, cancellationToken: ct));
        return raw;
    }

    public async Task<bool> ValidateAndConsumeAsync(string token, CancellationToken ct)
    {
        const string sql = """
            UPDATE nip_bootstrap_tokens
            SET consumed = true
            WHERE token_hash = @Hash
              AND consumed = false
              AND revoked = false
              AND expires_at > now()
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Hash = Hash(token) }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<IReadOnlyList<BootstrapTokenInfo>> ListAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id, label, created_at, expires_at, consumed, revoked
            FROM nip_bootstrap_tokens
            ORDER BY created_at DESC
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<TokenRow>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new BootstrapTokenInfo(
            r.Id, r.Label, r.Created_at, r.Expires_at, r.Consumed, r.Revoked)).ToList();
    }

    public async Task<bool> RevokeAsync(string tokenId, CancellationToken ct)
    {
        const string sql = """
            UPDATE nip_bootstrap_tokens
            SET revoked = true
            WHERE id = @Id AND consumed = false AND revoked = false
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            sql, new { Id = tokenId }, cancellationToken: ct));
        return rows > 0;
    }

    public int PendingCount
    {
        get
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            return conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM nip_pending_requests WHERE status = 'pending'");
        }
    }

    public async Task<string> EnqueueAsync(PendingRegistration request, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO nip_pending_requests
                (id, entity_type, identifier, pub_key, capabilities, scope_json,
                 metadata_json, requested_at, status, reject_reason)
            VALUES
                (@Id, @EntityType, @Identifier, @PubKey, @Capabilities, @ScopeJson::jsonb,
                 @MetadataJson::jsonb, @RequestedAt, @Status, @RejectReason)
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            request.Id,
            request.EntityType,
            request.Identifier,
            request.PubKey,
            Capabilities = request.Capabilities.ToArray(),
            request.ScopeJson,
            MetadataJson = request.MetadataJson ?? "null",
            RequestedAt  = request.RequestedAt.ToUniversalTime(),
            Status       = ToDbStatus(request.Status),
            request.RejectReason,
        }, cancellationToken: ct));
        return request.Id;
    }

    async Task<IReadOnlyList<PendingRegistration>> IPendingStore.ListAsync(CancellationToken ct)
    {
        const string sql = "SELECT * FROM nip_pending_requests ORDER BY requested_at DESC";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<PendingRow>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(MapPending).ToList();
    }

    public async Task<PendingRegistration?> GetAsync(string id, CancellationToken ct)
    {
        const string sql = "SELECT * FROM nip_pending_requests WHERE id = @Id LIMIT 1";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<PendingRow>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
        return row is null ? null : MapPending(row);
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

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id     = id,
            Status = ToDbStatus(status),
            Reason = reason,
        }, cancellationToken: ct));
        return rows > 0;
    }

    private static PendingRegistration MapPending(PendingRow row) => new(
        row.Id,
        row.Entity_type,
        row.Identifier,
        row.Pub_key,
        row.Capabilities,
        row.Scope_json,
        row.Metadata_json,
        row.Requested_at,
        FromDbStatus(row.Status),
        row.Reject_reason);

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

    private sealed class TokenRow
    {
        public string Id { get; set; } = "";
        public string? Label { get; set; }
        public DateTimeOffset Created_at { get; set; }
        public DateTimeOffset Expires_at { get; set; }
        public bool Consumed { get; set; }
        public bool Revoked { get; set; }
    }

    private sealed class PendingRow
    {
        public string Id { get; set; } = "";
        public string Entity_type { get; set; } = "";
        public string Identifier { get; set; } = "";
        public string Pub_key { get; set; } = "";
        public string[] Capabilities { get; set; } = [];
        public string Scope_json { get; set; } = "{}";
        public string? Metadata_json { get; set; }
        public DateTimeOffset Requested_at { get; set; }
        public string Status { get; set; } = "pending";
        public string? Reject_reason { get; set; }
    }
}
