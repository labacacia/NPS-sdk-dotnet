// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NPS.NIP.Ca;
using NPS.NIP.Ca.Ra;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;
using NPS.NIP;

namespace NPS.NIP.Http;

/// <summary>
/// ASP.NET Core minimal-API route handlers for the NIP CA Server (NPS-3 §8).
/// Maps the portable NIP CA API endpoints + <c>/.well-known/nps-ca</c>.
/// Mount via <see cref="NipCaRouterExtensions.MapNipCa"/>.
/// </summary>
public static class NipCaRouter
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented               = false,
    };

    private static readonly Regex s_identifierRe =
        new(@"^[a-zA-Z0-9._:@/\-]{1,256}$", RegexOptions.Compiled);

    private static readonly HashSet<string> s_validRevocationReasons = new(StringComparer.Ordinal)
    {
        "key_compromise", "ca_compromise", "affiliation_changed",
        "superseded", "cessation_of_operation",
        "parent_revoked", // NPS-CR-0003 §5.3
    };

    /// <summary>
    /// Registers all NIP CA routes on <paramref name="app"/> under
    /// the configured <see cref="NipCaOptions.RoutePrefix"/>.
    /// </summary>
    public static void MapNipCa(
        IEndpointRouteBuilder  app,
        NipCaOptions           opts,
        NipCaService           ca,
        IBootstrapTokenStore?  bootstrapTokenStore = null,
        IPendingStore?         pendingStore        = null)
    {
        var pfx = opts.RoutePrefix.TrimEnd('/');

        var enrollmentPolicy = NipCaService.CreateEnrollmentPolicy(opts, bootstrapTokenStore, pendingStore);

        // ── Discovery ─────────────────────────────────────────────────────────

        app.MapGet("/.well-known/nps-ca", (HttpContext ctx) =>
        {
            var body = new
            {
                nps_ca       = "0.1",
                issuer       = opts.CaNid,
                display_name = opts.DisplayName,
                public_key   = ca.GetCaPublicKey(),
                algorithms   = opts.Algorithms,
                endpoints    = new
                {
                    register = $"{opts.BaseUrl}{pfx}/v1/agents/register",
                    verify   = $"{opts.BaseUrl}{pfx}/v1/agents/{{nid}}/verify",
                    ocsp     = $"{opts.BaseUrl}{pfx}/v1/agents/{{nid}}/verify",
                    node_ocsp = $"{opts.BaseUrl}{pfx}/v1/nodes/{{nid}}/verify",
                    crl      = $"{opts.BaseUrl}{pfx}/v1/crl",
                },
                capabilities          = new[] { "agent", "node", "orchestrator-group",
                    $"ra-tier-{(int)opts.EnrollmentTier}" },
                max_cert_validity_days = opts.AgentCertValidityDays,
            };
            return Results.Json(body, s_json);
        });

        // ── CA public key ─────────────────────────────────────────────────────

        app.MapGet($"{pfx}/v1/ca/cert", () =>
            Results.Json(new { public_key = ca.GetCaPublicKey(), algorithm = "ed25519" }, s_json));

        // ── CRL ───────────────────────────────────────────────────────────────

        app.MapGet($"{pfx}/v1/crl", async (CancellationToken ct) =>
        {
            var revoked = await ca.GetCrlAsync(ct);
            var entries = revoked
                .OrderBy(r => r.RevokedAt)
                .ThenBy(r => r.Serial, StringComparer.Ordinal)
                .ThenBy(r => r.Nid, StringComparer.Ordinal)
                .Select(r => new
                {
                    nid         = r.Nid,
                    serial      = r.Serial,
                    revoked_at  = r.RevokedAt?.ToString("O"),
                    reason      = r.RevokeReason,
                })
                .ToList();
            var body = new
            {
                issued_by = opts.CaNid,
                issued_at = DateTime.UtcNow.ToString("O"),
                entries,
            };
            return Results.Json(new
            {
                body.issued_by,
                body.issued_at,
                body.entries,
                signature = ca.SignArtifact(body),
            }, s_json);
        });

        // ── Certificate enumeration (operator audit) ─────────────────────────

        app.MapGet($"{pfx}/v1/certificates", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();

            var records = await ca.ListCertificatesAsync(ct);
            var entries = records
                .OrderBy(r => r.IssuedAt)
                .ThenBy(r => r.Serial, StringComparer.Ordinal)
                .Select(r => new
                {
                    nid = r.Nid,
                    entity_type = r.EntityType,
                    serial = r.Serial,
                    pub_key = r.PubKey,
                    capabilities = r.Capabilities,
                    scope = JsonDocument.Parse(r.ScopeJson).RootElement.Clone(),
                    issued_by = r.IssuedBy,
                    issued_at = r.IssuedAt.ToString("O"),
                    expires_at = r.ExpiresAt.ToString("O"),
                    revoked_at = r.RevokedAt?.ToString("O"),
                    revoke_reason = r.RevokeReason,
                    nid_role = r.NidRole,
                    parent_nid = r.ParentNid,
                })
                .ToList();
            return Results.Json(new { entries }, s_json);
        });

        // ── Agent registration ────────────────────────────────────────────────

        app.MapPost($"{pfx}/v1/agents/register", async (HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();

            var req = await ReadJson<RegisterRequest>(ctx, log, ct);
            if (req is null) return BadRequest("Invalid JSON body.");

            if (!ValidateRegisterRequest(req, out var err)) return BadRequest(err!);

            var enrollToken = ctx.Request.Headers["X-NPS-Enrollment-Token"].FirstOrDefault();
            try
            {
                var frame = await ca.RegisterWithRaAsync(
                    "agent", req.Identifier!, req.PubKey!,
                    req.Capabilities ?? [],
                    req.ScopeJson    ?? "{}",
                    req.MetadataJson,
                    enrollToken,
                    enrollmentPolicy,
                    ct);
                return Results.Json(frame, s_json, statusCode: 201);
            }
            catch (NipRaPendingException ex)
            {
                return Results.Json(new { pending_id = ex.PendingId, status = "queued" }, s_json, statusCode: 202);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Register agent failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Node registration ─────────────────────────────────────────────────

        app.MapPost($"{pfx}/v1/nodes/register", async (HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();

            var req = await ReadJson<RegisterRequest>(ctx, log, ct);
            if (req is null) return BadRequest("Invalid JSON body.");

            if (!ValidateRegisterRequest(req, out var err)) return BadRequest(err!);

            var enrollToken = ctx.Request.Headers["X-NPS-Enrollment-Token"].FirstOrDefault();
            try
            {
                var frame = await ca.RegisterWithRaAsync(
                    "node", req.Identifier!, req.PubKey!,
                    req.Capabilities ?? ["nwp:query", "nwp:stream"],
                    req.ScopeJson    ?? "{}",
                    enrollmentToken:  enrollToken,
                    enrollmentPolicy: enrollmentPolicy,
                    ct: ct);
                return Results.Json(frame, s_json, statusCode: 201);
            }
            catch (NipRaPendingException ex)
            {
                return Results.Json(new { pending_id = ex.PendingId, status = "queued" }, s_json, statusCode: 202);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Register node failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Agent renew ───────────────────────────────────────────────────────

        app.MapPost($"{pfx}/v1/agents/{{nid}}/renew", async (string nid, HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();
            try
            {
                var frame = await ca.RenewAsync(Uri.UnescapeDataString(nid), ct);
                return Results.Json(frame, s_json);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Renew agent failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Node renew ────────────────────────────────────────────────────────

        app.MapPost($"{pfx}/v1/nodes/{{nid}}/renew", async (string nid, HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();
            try
            {
                var frame = await ca.RenewAsync(Uri.UnescapeDataString(nid), ct);
                return Results.Json(frame, s_json);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Renew node failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Agent revoke ──────────────────────────────────────────────────────

        app.MapPost($"{pfx}/v1/agents/{{nid}}/revoke", async (string nid, HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();

            var req    = await ReadJson<RevokeRequest>(ctx, log, ct);
            var reason = req?.Reason ?? "cessation_of_operation";

            if (!s_validRevocationReasons.Contains(reason))
                return BadRequest($"Invalid revocation reason '{reason}'. Allowed: {string.Join(", ", s_validRevocationReasons)}.");

            try
            {
                var frame = await ca.RevokeAsync(Uri.UnescapeDataString(nid), reason, ct);
                return Results.Json(frame, s_json);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Revoke agent failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Node revoke ───────────────────────────────────────────────────────

        app.MapPost($"{pfx}/v1/nodes/{{nid}}/revoke", async (string nid, HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();

            var req    = await ReadJson<RevokeRequest>(ctx, log, ct);
            var reason = req?.Reason ?? "cessation_of_operation";

            if (!s_validRevocationReasons.Contains(reason))
                return BadRequest($"Invalid revocation reason '{reason}'. Allowed: {string.Join(", ", s_validRevocationReasons)}.");

            try
            {
                var frame = await ca.RevokeAsync(Uri.UnescapeDataString(nid), reason, ct);
                return Results.Json(frame, s_json);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Revoke node failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Agent register-x509 (NPS-RFC-0002) ───────────────────────────────

        app.MapPost($"{pfx}/v1/agents/register-x509", async (HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();

            var req = await ReadJson<RegisterX509Request>(ctx, log, ct);
            if (req is null) return BadRequest("Invalid JSON body.");

            if (!ValidateRegisterRequest(new RegisterRequest { Identifier = req.Identifier, PubKey = req.PubKey }, out var err))
                return BadRequest(err!);

            try
            {
                var frame = await ca.RegisterX509Async(
                    "agent", req.Identifier!, req.PubKey!,
                    req.Capabilities   ?? [],
                    req.ScopeJson      ?? "{}",
                    assuranceLevel:    ParseAssuranceLevel(req.AssuranceLevel),
                    metadataJson:      req.MetadataJson,
                    ct:                ct);
                return Results.Json(frame, s_json, statusCode: 201);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Register agent X.509 failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Node register-x509 (NPS-RFC-0002) ────────────────────────────────

        app.MapPost($"{pfx}/v1/nodes/register-x509", async (HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();

            var req = await ReadJson<RegisterX509Request>(ctx, log, ct);
            if (req is null) return BadRequest("Invalid JSON body.");

            if (!ValidateRegisterRequest(new RegisterRequest { Identifier = req.Identifier, PubKey = req.PubKey }, out var err))
                return BadRequest(err!);

            try
            {
                var frame = await ca.RegisterX509Async(
                    "node", req.Identifier!, req.PubKey!,
                    req.Capabilities   ?? ["nwp:query", "nwp:stream"],
                    req.ScopeJson      ?? "{}",
                    assuranceLevel:    ParseAssuranceLevel(req.AssuranceLevel),
                    metadataJson:      req.MetadataJson,
                    ct:                ct);
                return Results.Json(frame, s_json, statusCode: 201);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Register node X.509 failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Agent verify (OCSP) ───────────────────────────────────────────────

        app.MapGet($"{pfx}/v1/agents/{{nid}}/verify", async (string nid, CancellationToken ct) =>
            OcspResult(await VerifyWithTiming(ca, opts, nid, ct)));

        // ── Node verify (OCSP) ────────────────────────────────────────────────

        app.MapGet($"{pfx}/v1/nodes/{{nid}}/verify", async (string nid, CancellationToken ct) =>
            OcspResult(await VerifyWithTiming(ca, opts, nid, ct)));

        // ── Orchestrator group: register (NPS-CR-0003) ────────────────────────

        app.MapPost($"{pfx}/v1/orchestrators/groups/register", async (HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();

            var req = await ReadJson<RegisterGroupRequest>(ctx, log, ct);
            if (req is null) return BadRequest("Invalid JSON body.");

            if (!string.IsNullOrEmpty(req.Identifier) && !s_identifierRe.IsMatch(req.Identifier))
                return BadRequest("identifier contains invalid characters. Allowed: a-z A-Z 0-9 . _ : @ / -");
            if (string.IsNullOrEmpty(req.PubKey) ||
                !req.PubKey.StartsWith("ed25519:", StringComparison.Ordinal) || req.PubKey.Length <= 8)
                return BadRequest("pub_key must be 'ed25519:<base64url>'.");

            try
            {
                var frame = await ca.RegisterGroupAsync(
                    identifier:   req.Identifier,
                    pubKey:       req.PubKey,
                    capabilities: req.Capabilities ?? [],
                    scopeJson:    req.ScopeJson    ?? "{}",
                    ownerUserId:  req.OwnerUserId,
                    ownerKeyId:   req.OwnerKeyId,
                    metadataJson: req.MetadataJson,
                    ct:           ct);
                return Results.Json(frame, s_json, statusCode: 201);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Register group failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Orchestrator group: issue session (NPS-CR-0003) ───────────────────

        app.MapPost($"{pfx}/v1/orchestrators/groups/{{group_nid}}/sessions/issue", async (
            string group_nid, HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            var groupNid = Uri.UnescapeDataString(group_nid);

            // Two auth modes: Operator API key (Bearer) OR group-JWS body.
            // We pick the mode by Content-Type so the operator path stays
            // a plain JSON body, matching the rest of the surface.
            var ctype     = ctx.Request.ContentType ?? "";
            var isJwsBody = ctype.Contains("jose+json", StringComparison.OrdinalIgnoreCase);

            IssueSessionRequest? req;
            string?              errorCode = null;

            if (isJwsBody)
            {
                // Group-JWS path
                var jws = await ReadJson<NipGroupJws.FlattenedJws>(ctx, log, ct);
                if (jws is null) return BadRequest("Invalid JWS body.");

                var groupRecord = await ca.GetCertAsync(groupNid, ct);
                if (groupRecord is null)
                    return ErrorResult(new NipCaException(
                        $"Group {groupNid} not found.", NipErrorCodes.ParentNotFound));
                if (groupRecord.NidRole != IdentLineageRole.Group)
                    return ErrorResult(new NipCaException(
                        $"NID {groupNid} is not a group.", NipErrorCodes.ParentNotGroup));
                if (groupRecord.RevokedAt.HasValue)
                    return ErrorResult(new NipCaException(
                        $"Group {groupNid} revoked.", NipErrorCodes.GroupRevoked));

                var pubKey = NipSigner.DecodePublicKey(groupRecord.PubKey);
                if (pubKey is null)
                    return Results.Json(new
                    {
                        error_code = NipErrorCodes.JwsInvalid,
                        message    = "Group public key could not be decoded.",
                    }, s_json, statusCode: 401);

                if (!NipGroupJws.TryVerify(jws, pubKey, out var payloadJson, out var kid, out errorCode))
                    return Results.Json(new
                    {
                        error_code = errorCode,
                        message    = "Group-JWS verification failed.",
                    }, s_json, statusCode: 401);

                if (!string.Equals(kid, groupNid, StringComparison.Ordinal))
                    return Results.Json(new
                    {
                        error_code = NipErrorCodes.JwsInvalid,
                        message    = $"JWS kid '{kid}' does not match URL group_nid '{groupNid}'.",
                    }, s_json, statusCode: 401);

                IssueSessionPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<IssueSessionPayload>(payloadJson!, s_json);
                }
                catch
                {
                    return Results.Json(new
                    {
                        error_code = NipErrorCodes.JwsInvalid,
                        message    = "JWS payload is not valid JSON.",
                    }, s_json, statusCode: 401);
                }
                if (payload is null)
                    return Results.Json(new
                    {
                        error_code = NipErrorCodes.JwsInvalid,
                        message    = "JWS payload missing.",
                    }, s_json, statusCode: 401);

                var skewSec  = (long)opts.SessionJwsClockSkew.TotalSeconds;
                var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (payload.Iat == 0 || Math.Abs(nowEpoch - payload.Iat) > skewSec)
                    return Results.Json(new
                    {
                        error_code = NipErrorCodes.JwsExpired,
                        message    = $"JWS iat outside ±{skewSec}s window.",
                    }, s_json, statusCode: 401);

                req = new IssueSessionRequest
                {
                    SessionPubKey   = payload.SessionPubKey,
                    Purpose         = payload.Purpose,
                    ValiditySeconds = payload.ValiditySeconds,
                    Capabilities    = payload.Capabilities,
                    ScopeJson       = payload.ScopeJson,
                    MetadataJson    = payload.MetadataJson,
                };
            }
            else
            {
                // Operator API key path
                if (!IsAuthorized(ctx, opts)) return Unauthorized();
                req = await ReadJson<IssueSessionRequest>(ctx, log, ct);
                if (req is null) return BadRequest("Invalid JSON body.");
            }

            if (string.IsNullOrEmpty(req.SessionPubKey) ||
                !req.SessionPubKey.StartsWith("ed25519:", StringComparison.Ordinal) ||
                req.SessionPubKey.Length <= 8)
                return BadRequest("session_pub_key must be 'ed25519:<base64url>'.");

            TimeSpan? validity = req.ValiditySeconds is > 0
                ? TimeSpan.FromSeconds(req.ValiditySeconds.Value)
                : null;

            try
            {
                var frame = await ca.IssueSessionAsync(
                    groupNid:      groupNid,
                    sessionPubKey: req.SessionPubKey,
                    validity:      validity,
                    purpose:       req.Purpose,
                    capabilities:  req.Capabilities,
                    scopeJson:     req.ScopeJson,
                    metadataJson:  req.MetadataJson,
                    ct:            ct);
                return Results.Json(frame, s_json, statusCode: 201);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Issue session failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Orchestrator group: revoke (NPS-CR-0003) ─────────────────────────

        app.MapPost($"{pfx}/v1/orchestrators/groups/{{group_nid}}/revoke", async (
            string group_nid, HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();

            var req    = await ReadJson<RevokeRequest>(ctx, log, ct);
            var reason = req?.Reason ?? "cessation_of_operation";

            if (!s_validRevocationReasons.Contains(reason))
                return BadRequest($"Invalid revocation reason '{reason}'. Allowed: {string.Join(", ", s_validRevocationReasons)}.");

            try
            {
                var frame = await ca.RevokeAsync(Uri.UnescapeDataString(group_nid), reason, ct);
                return Results.Json(frame, s_json);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Revoke group failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Orchestrator group: list sessions (NPS-CR-0003 audit) ────────────

        app.MapGet($"{pfx}/v1/orchestrators/groups/{{group_nid}}/sessions", async (
            string group_nid, HttpContext ctx, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();
            var groupNid = Uri.UnescapeDataString(group_nid);
            var sessions = await ca.ListSessionsAsync(groupNid, ct);
            return Results.Json(new
            {
                group_nid = groupNid,
                count     = sessions.Count,
                sessions  = sessions.Select(s => new
                {
                    nid         = s.Nid,
                    serial      = s.Serial,
                    issued_at   = s.IssuedAt.ToString("O"),
                    expires_at  = s.ExpiresAt.ToString("O"),
                    revoked_at  = s.RevokedAt?.ToString("O"),
                    revoke_reason = s.RevokeReason,
                }),
            }, s_json);
        });

        // ── Enrollment: create bootstrap token (NPS-CR-0005 §3.3) ───────────

        app.MapPost($"{pfx}/v1/enrollment/tokens", async (HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();
            if (bootstrapTokenStore is null)
                return Results.Json(new { error_code = "NIP-CA-BAD-REQUEST",
                    message = "Bootstrap token enrollment is not enabled on this CA." }, s_json, statusCode: 400);

            var req = await ReadJson<CreateTokenRequest>(ctx, log, ct);
            var ttl = req?.TtlSeconds is > 0
                ? TimeSpan.FromSeconds(req.TtlSeconds.Value)
                : opts.BootstrapTokenMaxTtl;
            if (ttl > opts.BootstrapTokenMaxTtl) ttl = opts.BootstrapTokenMaxTtl;

            var expiresAt = DateTimeOffset.UtcNow + ttl;
            var raw = await bootstrapTokenStore.CreateAsync(req?.Label, expiresAt, ct);
            return Results.Json(new { token = raw, expires_at = expiresAt, label = req?.Label }, s_json, statusCode: 201);
        });

        // ── Enrollment: list pending (NPS-CR-0005 §3.4) ──────────────────────

        app.MapGet($"{pfx}/v1/enrollment/pending", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();
            if (pendingStore is null)
                return Results.Json(new { error_code = "NIP-CA-BAD-REQUEST",
                    message = "Pending-queue enrollment is not enabled on this CA." }, s_json, statusCode: 400);

            var records = await pendingStore.ListAsync(ct);
            var items = records.Select(r => new
            {
                id           = r.Id,
                entity_type  = r.EntityType,
                identifier   = r.Identifier,
                pub_key      = r.PubKey,
                capabilities = r.Capabilities,
                scope_json   = r.ScopeJson,
                requested_at = r.RequestedAt,
                status       = r.Status.ToString().ToLowerInvariant(),
                reject_reason = r.RejectReason,
            });
            return Results.Json(new { count = records.Count, items }, s_json);
        });

        // ── Enrollment: approve pending (NPS-CR-0005 §3.4) ───────────────────

        app.MapPost($"{pfx}/v1/enrollment/pending/{{id}}/approve", async (
            string id, HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();
            if (pendingStore is null)
                return Results.Json(new { error_code = "NIP-CA-BAD-REQUEST",
                    message = "Pending-queue enrollment is not enabled on this CA." }, s_json, statusCode: 400);

            var record = await pendingStore.GetAsync(id, ct);
            if (record is null)
                return Results.Json(new { error_code = NipErrorCodes.NidNotFound,
                    message = $"Pending registration '{id}' not found." }, s_json, statusCode: 404);

            if (record.Status != PendingStatus.Pending)
                return Results.Json(new { error_code = "NIP-CA-BAD-REQUEST",
                    message = $"Record '{id}' is already {record.Status}." }, s_json, statusCode: 409);

            try
            {
                var frame = await ca.RegisterAsync(
                    record.EntityType, record.Identifier, record.PubKey,
                    record.Capabilities, record.ScopeJson, record.MetadataJson, ct);
                await pendingStore.ApproveAsync(id, ct);
                return Results.Json(frame, s_json, statusCode: 201);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Approve pending {Id} failed: {Msg}", id, ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Enrollment: reject pending (NPS-CR-0005 §3.4) ────────────────────

        app.MapPost($"{pfx}/v1/enrollment/pending/{{id}}/reject", async (
            string id, HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            if (!IsAuthorized(ctx, opts)) return Unauthorized();
            if (pendingStore is null)
                return Results.Json(new { error_code = "NIP-CA-BAD-REQUEST",
                    message = "Pending-queue enrollment is not enabled on this CA." }, s_json, statusCode: 400);

            var req    = await ReadJson<RejectPendingRequest>(ctx, log, ct);
            var reason = req?.Reason ?? "rejected_by_operator";

            var ok = await pendingStore.RejectAsync(id, reason, ct);
            if (!ok)
            {
                var record = await pendingStore.GetAsync(id, ct);
                var msg = record is null
                    ? $"Pending registration '{id}' not found."
                    : $"Record '{id}' is already {record.Status}.";
                return Results.Json(new { error_code = "NIP-CA-BAD-REQUEST", message = msg },
                    s_json, statusCode: record is null ? 404 : 409);
            }

            log.LogInformation("Pending registration {Id} rejected: {Reason}", id, reason);
            return Results.Json(new { id, status = "rejected", reason }, s_json);
        });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<NipVerifyResult> VerifyWithTiming(
        NipCaService ca, NipCaOptions opts, string rawNid, CancellationToken ct)
    {
        var nid = Uri.UnescapeDataString(rawNid);
        if (!opts.NormalizeOcspResponseTime)
            return await ca.VerifyAsync(nid, ct);

        var sw     = System.Diagnostics.Stopwatch.StartNew();
        var result = await ca.VerifyAsync(nid, ct);
        var delay  = (int)(200 - sw.ElapsedMilliseconds);
        if (delay > 0) await Task.Delay(delay, ct);
        return result;
    }

    private static bool IsAuthorized(HttpContext ctx, NipCaOptions opts)
    {
        if (opts.OperatorApiKey is null) return true;
        var header = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;
        var provided = header["Bearer ".Length..].Trim();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(opts.OperatorApiKey));
    }

    private static bool ValidateRegisterRequest(RegisterRequest req, out string? error)
    {
        if (string.IsNullOrEmpty(req.Identifier) || string.IsNullOrEmpty(req.PubKey))
        {
            error = "identifier and pub_key are required.";
            return false;
        }
        if (!s_identifierRe.IsMatch(req.Identifier))
        {
            error = "identifier contains invalid characters. Allowed: a-z A-Z 0-9 . _ : @ / -";
            return false;
        }
        if (!req.PubKey.StartsWith("ed25519:", StringComparison.Ordinal) || req.PubKey.Length <= 8)
        {
            error = "pub_key must be 'ed25519:<base64url>'.";
            return false;
        }
        error = null;
        return true;
    }

    private static IResult OcspResult(NipVerifyResult r)
    {
        if (r.Valid)
            return Results.Json(new
            {
                valid      = true,
                nid        = r.Record!.Nid,
                expires_at = r.Record.ExpiresAt.ToString("O"),
                serial     = r.Record.Serial,
            }, s_json);

        var statusCode = r.ErrorCode == NipErrorCodes.NidNotFound ? 404 : 200;
        return Results.Json(new
        {
            valid      = false,
            error_code = r.ErrorCode,
            message    = r.Message,
        }, s_json, statusCode: statusCode);
    }

    private static IResult ErrorResult(NipCaException ex)
    {
        var status = ex.ErrorCode switch
        {
            NipErrorCodes.NidNotFound            => 404,
            NipErrorCodes.ParentNotFound         => 404,
            NipErrorCodes.NidAlreadyExists       => 409,
            NipErrorCodes.SerialDuplicate        => 409,
            NipErrorCodes.RenewalTooEarly        => 400,
            NipErrorCodes.SessionValidityInvalid => 400,
            NipErrorCodes.ParentNotGroup         => 400,
            NipErrorCodes.ScopeExpansion         => 403,
            NipErrorCodes.CertCapMissing         => 403,
            NipErrorCodes.GroupRevoked           => 403,
            NipErrorCodes.JwsInvalid             => 401,
            NipErrorCodes.JwsExpired             => 401,
            NipErrorCodes.CertExpired            => 401,
            NipErrorCodes.CertRevoked            => 401,
            NipErrorCodes.ParentRevoked          => 401,
            NipErrorCodes.RaTokenInvalid         => 401,
            NipErrorCodes.RaTokenExpired         => 401,
            NipErrorCodes.RaNidNotAllowed        => 403,
            NipErrorCodes.RaPendingRejected      => 403,
            _                                    => 400,
        };
        return Results.Json(new { error_code = ex.ErrorCode, message = ex.Message }, s_json, statusCode: status);
    }

    private static IResult BadRequest(string msg) =>
        Results.Json(new { error_code = "NIP-CA-BAD-REQUEST", message = msg }, s_json, statusCode: 400);

    private static IResult Unauthorized() =>
        Results.Json(new { error_code = "NIP-CA-UNAUTHORIZED", message = "Valid operator Bearer token required." },
            s_json, statusCode: 401);

    private static async Task<T?> ReadJson<T>(HttpContext ctx, ILogger log, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms.Length == 0 ? default : JsonSerializer.Deserialize<T>(ms.ToArray(), s_json);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to deserialize {Type} from request body", typeof(T).Name);
            return default;
        }
    }

    private static AssuranceLevel ParseAssuranceLevel(string? raw) => raw?.ToLowerInvariant() switch
    {
        "attested" => AssuranceLevel.Attested,
        "verified" => AssuranceLevel.Verified,
        _          => AssuranceLevel.Anonymous,
    };

    // ── Request DTOs ─────────────────────────────────────────────────────────

    private sealed class RegisterRequest
    {
        public string?                Identifier   { get; set; }
        public string?                PubKey       { get; set; }
        public IReadOnlyList<string>? Capabilities { get; set; }
        public string?                ScopeJson    { get; set; }
        public string?                MetadataJson { get; set; }
    }

    private sealed class RevokeRequest
    {
        public string? Reason { get; set; }
    }

    private sealed class RegisterX509Request
    {
        public string?                Identifier     { get; set; }
        public string?                PubKey         { get; set; }
        public IReadOnlyList<string>? Capabilities   { get; set; }
        public string?                ScopeJson      { get; set; }
        public string?                MetadataJson   { get; set; }
        public string?                AssuranceLevel { get; set; }
    }

    /// <summary>
    /// Body for <c>POST /v1/orchestrators/groups/register</c> (NPS-CR-0003).
    /// Operator-authed — Operator-API-key Bearer required.
    /// </summary>
    private sealed class RegisterGroupRequest
    {
        public string?                Identifier   { get; set; }
        public string?                PubKey       { get; set; }
        public IReadOnlyList<string>? Capabilities { get; set; }
        public string?                ScopeJson    { get; set; }
        public string?                MetadataJson { get; set; }
        public string?                OwnerUserId  { get; set; }
        public string?                OwnerKeyId   { get; set; }
    }

    /// <summary>
    /// Body for <c>POST /v1/orchestrators/groups/{group_nid}/sessions/issue</c>
    /// when authorising via Operator API key (NPS-CR-0003 §8). Plain JSON.
    /// </summary>
    private sealed class IssueSessionRequest
    {
        public string?                SessionPubKey   { get; set; }
        public string?                Purpose         { get; set; }
        public int?                   ValiditySeconds { get; set; }
        public IReadOnlyList<string>? Capabilities    { get; set; }
        public string?                ScopeJson       { get; set; }
        public string?                MetadataJson    { get; set; }
    }

    private sealed class CreateTokenRequest
    {
        public int?    TtlSeconds { get; set; }
        public string? Label      { get; set; }
    }

    private sealed class RejectPendingRequest
    {
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Decoded JWS payload for the same endpoint when authorising via
    /// group-JWS (NPS-CR-0003 §3.5). The wire JSON keys are snake_case so
    /// the operator and JWS payloads are interchangeable.
    /// </summary>
    private sealed class IssueSessionPayload
    {
        public string?                SessionPubKey   { get; set; }
        public string?                Purpose         { get; set; }
        public int?                   ValiditySeconds { get; set; }
        public IReadOnlyList<string>? Capabilities    { get; set; }
        public string?                ScopeJson       { get; set; }
        public string?                MetadataJson    { get; set; }
        public long                   Iat             { get; set; }
    }
}

/// <summary>Extension method to mount NIP CA routes.</summary>
public static class NipCaRouterExtensions
{
    /// <summary>
    /// Calls <see cref="NipCaRouter.MapNipCa"/> to register all NIP CA routes.
    /// Requires <c>AddNipCa()</c> to have been called in the DI setup.
    /// </summary>
    public static IEndpointRouteBuilder MapNipCa(
        this IEndpointRouteBuilder app,
        NipCaOptions opts,
        NipCaService ca)
    {
        NipCaRouter.MapNipCa(app, opts, ca);
        return app;
    }
}
