// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NPS.NIP.Ca;

namespace NPS.NIP.Http;

/// <summary>
/// ASP.NET Core minimal-API route handlers for the NIP CA Server (NPS-3 §8).
/// Maps the 8 API endpoints + <c>/.well-known/nps-ca</c>.
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

    /// <summary>
    /// Registers all NIP CA routes on <paramref name="app"/> under
    /// the configured <see cref="NipCaOptions.RoutePrefix"/>.
    /// </summary>
    public static void MapNipCa(IEndpointRouteBuilder app, NipCaOptions opts, NipCaService ca)
    {
        var pfx = opts.RoutePrefix.TrimEnd('/');

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
                    ocsp     = $"{opts.BaseUrl}{pfx}/ocsp",
                    crl      = $"{opts.BaseUrl}{pfx}/v1/crl",
                },
                capabilities          = new[] { "agent", "node" },
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
            var entries = revoked.Select(r => new
            {
                nid         = r.Nid,
                serial      = r.Serial,
                revoked_at  = r.RevokedAt?.ToString("O"),
                reason      = r.RevokeReason,
            });
            return Results.Json(new { issued_by = opts.CaNid, entries }, s_json);
        });

        // ── Agent registration ────────────────────────────────────────────────

        app.MapPost($"{pfx}/v1/agents/register", async (HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            var req = await ReadJson<RegisterRequest>(ctx, ct);
            if (req is null) return BadRequest("Invalid JSON body.");

            if (string.IsNullOrEmpty(req.Identifier) || string.IsNullOrEmpty(req.PubKey))
                return BadRequest("identifier and pub_key are required.");

            try
            {
                var frame = await ca.RegisterAsync(
                    "agent", req.Identifier, req.PubKey,
                    req.Capabilities ?? [],
                    req.ScopeJson    ?? "{}",
                    req.MetadataJson,
                    ct);
                return Results.Json(frame, s_json, statusCode: 201);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Register failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Node registration ─────────────────────────────────────────────────

        app.MapPost($"{pfx}/v1/nodes/register", async (HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            var req = await ReadJson<RegisterRequest>(ctx, ct);
            if (req is null) return BadRequest("Invalid JSON body.");

            if (string.IsNullOrEmpty(req.Identifier) || string.IsNullOrEmpty(req.PubKey))
                return BadRequest("identifier and pub_key are required.");

            try
            {
                var frame = await ca.RegisterAsync(
                    "node", req.Identifier, req.PubKey,
                    req.Capabilities ?? ["nwp:query", "nwp:stream"],
                    req.ScopeJson    ?? "{}",
                    ct: ct);
                return Results.Json(frame, s_json, statusCode: 201);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Node register failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Renew ─────────────────────────────────────────────────────────────

        app.MapPost($"{pfx}/v1/agents/{{nid}}/renew", async (string nid, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            try
            {
                var frame = await ca.RenewAsync(Uri.UnescapeDataString(nid), ct);
                return Results.Json(frame, s_json);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Renew failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Revoke ────────────────────────────────────────────────────────────

        app.MapPost($"{pfx}/v1/agents/{{nid}}/revoke", async (string nid, HttpContext ctx, ILogger<NipCaService> log, CancellationToken ct) =>
        {
            var req = await ReadJson<RevokeRequest>(ctx, ct);
            var reason = req?.Reason ?? "cessation_of_operation";

            try
            {
                var frame = await ca.RevokeAsync(Uri.UnescapeDataString(nid), reason, ct);
                return Results.Json(frame, s_json);
            }
            catch (NipCaException ex)
            {
                log.LogWarning("Revoke failed: {Msg}", ex.Message);
                return ErrorResult(ex);
            }
        });

        // ── Verify (OCSP) ─────────────────────────────────────────────────────

        app.MapGet($"{pfx}/v1/agents/{{nid}}/verify", async (string nid, CancellationToken ct) =>
        {
            if (opts.NormalizeOcspResponseTime)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await ca.VerifyAsync(Uri.UnescapeDataString(nid), ct);
                var elapsed = sw.ElapsedMilliseconds;
                if (elapsed < 200) await Task.Delay((int)(200 - elapsed), ct);
                return OcspResult(result);
            }
            else
            {
                var result = await ca.VerifyAsync(Uri.UnescapeDataString(nid), ct);
                return OcspResult(result);
            }
        });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

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
            NipErrorCodes.NidNotFound      => 404,
            NipErrorCodes.NidAlreadyExists => 409,
            NipErrorCodes.SerialDuplicate  => 409,
            NipErrorCodes.RenewalTooEarly  => 400,
            NipErrorCodes.ScopeExpansion   => 403,
            _                              => 400,
        };
        return Results.Json(new { error_code = ex.ErrorCode, message = ex.Message }, s_json, statusCode: status);
    }

    private static IResult BadRequest(string msg) =>
        Results.Json(new { error_code = "NIP-CA-BAD-REQUEST", message = msg }, s_json, statusCode: 400);

    private static async Task<T?> ReadJson<T>(HttpContext ctx, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms.Length == 0 ? default : JsonSerializer.Deserialize<T>(ms.ToArray(), s_json);
        }
        catch { return default; }
    }

    // ── Request DTOs ─────────────────────────────────────────────────────────

    private sealed class RegisterRequest
    {
        public string?            Identifier   { get; set; }
        public string?            PubKey       { get; set; }
        public IReadOnlyList<string>? Capabilities { get; set; }
        public string?            ScopeJson    { get; set; }
        public string?            MetadataJson { get; set; }
    }

    private sealed class RevokeRequest
    {
        public string? Reason { get; set; }
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
