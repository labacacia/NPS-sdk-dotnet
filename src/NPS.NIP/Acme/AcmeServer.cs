// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NPS.NIP.Ca;
using NPS.NIP.Crypto;
using NPS.NIP.X509;
using NSec.Cryptography;

// Same disambiguation as Ed25519X509SignatureGenerator — both NSec and
// System.Security.Cryptography.X509Certificates expose `PublicKey`.
using NSecPublicKey = NSec.Cryptography.PublicKey;

namespace NPS.NIP.Acme;

/// <summary>
/// In-process ACME server (RFC 8555) extended with the
/// <c>agent-01</c> challenge type per NPS-RFC-0002 §4.4. The server runs as
/// ASP.NET Core middleware so test fixtures can host it under
/// <c>Microsoft.AspNetCore.TestHost.TestServer</c>.
///
/// <para><b>Scope of this prototype:</b> in-memory state only, single
/// challenge type (<c>agent-01</c>), single identifier per order, no nonce
/// uniqueness enforcement. Sufficient to demonstrate end-to-end ACME
/// issuance and to back the
/// <see cref="NipCaService.RegisterX509Async"/>-equivalent flow for
/// RFC §9 empirical data.</para>
/// </summary>
public sealed class AcmeServer
{
    private readonly AcmeServerOptions _opts;
    private readonly NipCaService      _ca;
    private readonly NipKeyManager     _caKeys;
    private readonly X509Certificate2  _caRoot;

    private readonly ConcurrentDictionary<string, AccountState>     _accounts   = new();
    private readonly ConcurrentDictionary<string, OrderState>       _orders     = new();
    private readonly ConcurrentDictionary<string, AuthorizationState> _authz    = new();
    private readonly ConcurrentDictionary<string, ChallengeState>   _challenges = new();
    private readonly ConcurrentDictionary<string, byte[]>           _certs      = new();
    private long _nonceCounter;

    public AcmeServer(
        AcmeServerOptions opts,
        NipCaService      ca,
        NipKeyManager     caKeys,
        X509Certificate2  caRoot)
    {
        _opts   = opts;
        _ca     = ca;
        _caKeys = caKeys;
        _caRoot = caRoot;
    }

    /// <summary>
    /// Maps a sub-route under <see cref="AcmeServerOptions.PathPrefix"/> to
    /// the correct ACME endpoint. Call once at startup.
    /// </summary>
    public void MapEndpoints(IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            var prefix = _opts.PathPrefix.TrimEnd('/');
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }
            var sub = path[prefix.Length..];

            switch ((sub, ctx.Request.Method))
            {
                case ("/directory",         "GET"):  await Directory(ctx); return;
                case ("/new-nonce",         "HEAD"):
                case ("/new-nonce",         "GET"):  NewNonce(ctx); return;
                case ("/new-account",       "POST"): await NewAccount(ctx); return;
                case ("/new-order",         "POST"): await NewOrder(ctx); return;
            }

            // Resource lookups: /authz/{id}, /chall/{id}, /finalize/{id}, /cert/{id}, /order/{id}
            if (ctx.Request.Method == "POST")
            {
                if (TryRouteResource(sub, "/authz/", out var authzId))
                {
                    await GetAuthz(ctx, authzId); return;
                }
                if (TryRouteResource(sub, "/chall/", out var challId))
                {
                    await RespondChallenge(ctx, challId); return;
                }
                if (TryRouteResource(sub, "/finalize/", out var orderForFinalize))
                {
                    await Finalize(ctx, orderForFinalize); return;
                }
                if (TryRouteResource(sub, "/cert/", out var certId))
                {
                    await GetCert(ctx, certId); return;
                }
                if (TryRouteResource(sub, "/order/", out var orderId))
                {
                    await GetOrder(ctx, orderId); return;
                }
            }

            await next();
        });
    }

    // ── Endpoints ───────────────────────────────────────────────────────────

    private Task Directory(HttpContext ctx)
    {
        var dir = new AcmeDirectory
        {
            NewNonce   = Url(ctx, "/new-nonce"),
            NewAccount = Url(ctx, "/new-account"),
            NewOrder   = Url(ctx, "/new-order"),
            Meta       = new AcmeDirectoryMeta { TermsOfService = Url(ctx, "/tos") },
        };
        return WriteJson(ctx, 200, dir);
    }

    private void NewNonce(HttpContext ctx)
    {
        ctx.Response.Headers["Replay-Nonce"] = MintNonce();
        ctx.Response.Headers.CacheControl    = "no-store";
        ctx.Response.StatusCode = 200;
    }

    private async Task NewAccount(HttpContext ctx)
    {
        AcmeJwsEnvelope envelope; AcmeProtectedHeader header; NSecPublicKey pubKey;
        try
        {
            envelope = await ReadEnvelope(ctx);
            (header, pubKey) = ResolveJwk(envelope);
            AcmeJws.Verify(envelope, pubKey);
        }
        catch (AcmeJwsException ex) { await WriteProblem(ctx, 400, "malformed", ex.Message); return; }

        var jwk        = header.Jwk
            ?? throw new InvalidOperationException("ResolveJwk should have ensured jwk is present.");
        var thumbprint = AcmeJws.Thumbprint(jwk);
        var accountUrl = Url(ctx, $"/account/{thumbprint}");

        _accounts.AddOrUpdate(thumbprint,
            _ => new AccountState(thumbprint, jwk, "valid"),
            (_, existing) => existing);

        ctx.Response.Headers["Location"]    = accountUrl;
        ctx.Response.Headers["Replay-Nonce"] = MintNonce();
        var resp = new AcmeAccount { Status = "valid" };
        await WriteJson(ctx, 201, resp);
    }

    private async Task NewOrder(HttpContext ctx)
    {
        AcmeJwsEnvelope envelope; AcmeProtectedHeader header; AccountState account;
        try
        {
            envelope = await ReadEnvelope(ctx);
            (header, account) = ResolveAccount(envelope);
            AcmeJws.Verify(envelope, AcmeJws.ImportJwk(account.Jwk));
        }
        catch (AcmeJwsException ex) { await WriteProblem(ctx, 400, "unauthorized", ex.Message); return; }

        var payload = JsonSerializer.Deserialize<AcmeNewOrderPayload>(
            NipSigner.FromBase64Url(envelope.Payload));
        if (payload?.Identifiers is null || payload.Identifiers.Count == 0)
        {
            await WriteProblem(ctx, 400, "malformed", "newOrder requires identifiers."); return;
        }

        var orderId = Guid.NewGuid().ToString("N");
        var authzId = Guid.NewGuid().ToString("N");
        var challId = Guid.NewGuid().ToString("N");
        var token   = NipSigner.Base64Url(RandomNumberGenerator.GetBytes(32));
        var ident   = payload.Identifiers[0];

        var challenge = new ChallengeState(
            Id: challId, Type: AcmeWire.ChallengeAgent01, Token: token,
            Status: "pending", AccountThumbprint: account.Thumbprint, AuthzId: authzId);
        var authz = new AuthorizationState(
            Id: authzId, OrderId: orderId, Identifier: ident,
            Status: "pending", ChallengeIds: new[] { challId });
        var order = new OrderState(
            Id: orderId, AccountThumbprint: account.Thumbprint, Identifier: ident,
            AuthzId: authzId, Status: "pending", CertId: null, Csr: null);

        _challenges[challId] = challenge;
        _authz[authzId]      = authz;
        _orders[orderId]     = order;

        ctx.Response.Headers["Location"]    = Url(ctx, $"/order/{orderId}");
        ctx.Response.Headers["Replay-Nonce"] = MintNonce();
        await WriteJson(ctx, 201, ToWire(order, ctx));
    }

    private async Task GetAuthz(HttpContext ctx, string authzId)
    {
        try { _ = await ReadEnvelope(ctx); }
        catch (AcmeJwsException ex) { await WriteProblem(ctx, 400, "malformed", ex.Message); return; }

        if (!_authz.TryGetValue(authzId, out var authz))
        {
            await WriteProblem(ctx, 404, "malformed", $"unknown authz '{authzId}'."); return;
        }
        ctx.Response.Headers["Replay-Nonce"] = MintNonce();
        await WriteJson(ctx, 200, ToWire(authz, ctx));
    }

    private async Task RespondChallenge(HttpContext ctx, string challId)
    {
        AcmeJwsEnvelope envelope; AcmeProtectedHeader header; AccountState account;
        try
        {
            envelope = await ReadEnvelope(ctx);
            (header, account) = ResolveAccount(envelope);
            AcmeJws.Verify(envelope, AcmeJws.ImportJwk(account.Jwk));
        }
        catch (AcmeJwsException ex) { await WriteProblem(ctx, 400, "unauthorized", ex.Message); return; }

        if (!_challenges.TryGetValue(challId, out var chall))
        {
            await WriteProblem(ctx, 404, "malformed", $"unknown challenge '{challId}'."); return;
        }
        if (chall.AccountThumbprint != account.Thumbprint)
        {
            await WriteProblem(ctx, 403, "unauthorized", "challenge does not belong to this account."); return;
        }

        // RFC-0002 §4.4: agent-01 — payload carries an Ed25519 signature
        // (made with the agent's NID private key) over the challenge token.
        var respPayload = JsonSerializer.Deserialize<AcmeChallengeRespondPayload>(
            NipSigner.FromBase64Url(envelope.Payload));
        if (respPayload?.AgentSignature is null)
        {
            chall = chall with { Status = "invalid" };
            _challenges[challId] = chall;
            await WriteProblem(ctx, 400, NipErrorCodes.AcmeChallengeFailed,
                "agent-01 challenge requires an agent_signature payload field.");
            return;
        }

        // For agent-01 the agent's identity key IS the account JWK in this
        // prototype: the agent's NID corresponds to the same key that signed
        // the order. In a multi-key world the agent key would be conveyed
        // via the CSR; we collapse them here for prototype simplicity.
        var sigBytes = NipSigner.FromBase64Url(respPayload.AgentSignature);
        var tokenBytes = Encoding.ASCII.GetBytes(chall.Token);
        var agentPub  = AcmeJws.ImportJwk(account.Jwk);

        if (!SignatureAlgorithm.Ed25519.Verify(agentPub, tokenBytes, sigBytes))
        {
            chall = chall with { Status = "invalid" };
            _challenges[challId] = chall;
            await WriteProblem(ctx, 400, NipErrorCodes.AcmeChallengeFailed,
                "agent-01 signature did not verify against the account public key.");
            return;
        }

        chall = chall with { Status = "valid", Validated = DateTimeOffset.UtcNow.ToString("O") };
        _challenges[challId] = chall;

        if (_authz.TryGetValue(chall.AuthzId, out var authz))
        {
            _authz[chall.AuthzId] = authz with { Status = "valid" };
            if (_orders.TryGetValue(authz.OrderId, out var ord))
                _orders[authz.OrderId] = ord with { Status = "ready" };
        }

        ctx.Response.Headers["Replay-Nonce"] = MintNonce();
        await WriteJson(ctx, 200, ToWire(chall, ctx));
    }

    private async Task Finalize(HttpContext ctx, string orderId)
    {
        AcmeJwsEnvelope envelope; AcmeProtectedHeader header; AccountState account;
        try
        {
            envelope = await ReadEnvelope(ctx);
            (header, account) = ResolveAccount(envelope);
            AcmeJws.Verify(envelope, AcmeJws.ImportJwk(account.Jwk));
        }
        catch (AcmeJwsException ex) { await WriteProblem(ctx, 400, "unauthorized", ex.Message); return; }

        if (!_orders.TryGetValue(orderId, out var order) || order.AccountThumbprint != account.Thumbprint)
        {
            await WriteProblem(ctx, 404, "malformed", $"unknown order '{orderId}'."); return;
        }
        if (order.Status != "ready")
        {
            await WriteProblem(ctx, 403, "orderNotReady", $"order is in status '{order.Status}'."); return;
        }

        var finalize = JsonSerializer.Deserialize<AcmeFinalizePayload>(
            NipSigner.FromBase64Url(envelope.Payload));
        if (finalize?.Csr is null)
        {
            await WriteProblem(ctx, 400, "malformed", "finalize requires a csr field."); return;
        }

        // Issue cert. agent-01 has already proven possession; we trust the
        // CSR's subject + pubkey verbatim (signature validation skipped —
        // see RFC-0002 §4.4 simplification).
        byte[] csrDer;
        try { csrDer = NipSigner.FromBase64Url(finalize.Csr); }
        catch (Exception ex) { await WriteProblem(ctx, 400, "malformed", $"invalid CSR encoding: {ex.Message}"); return; }

        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequest(csrDer, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.SkipSignatureValidation);
        }
        catch (Exception ex)
        {
            await WriteProblem(ctx, 400, NipErrorCodes.CertFormatInvalid,
                $"CSR could not be parsed: {ex.Message}");
            return;
        }

        var subjectCn = ExtractCn(csr.SubjectName);
        if (subjectCn is null || subjectCn != order.Identifier.Value)
        {
            await WriteProblem(ctx, 400, NipErrorCodes.CertSubjectNidMismatch,
                $"CSR subject CN '{subjectCn}' does not match order identifier '{order.Identifier.Value}'.");
            return;
        }
        var subjectRaw = Ed25519PublicKey.ExtractRaw(csr.PublicKey)
            ?? throw new InvalidOperationException("CSR pubkey is not Ed25519.");

        var leaf = NipX509Builder.IssueLeaf(
            subjectNid:       subjectCn,
            subjectPubKeyRaw: subjectRaw,
            caPrivateKey:     _caKeys.PrivateKey,
            issuerNid:        _opts.CaNid,
            role:             NipX509Builder.LeafRole.Agent,
            assuranceLevel:   AssuranceLevel.Anonymous,
            notBefore:        DateTimeOffset.UtcNow.AddMinutes(-1),
            notAfter:         DateTimeOffset.UtcNow.AddDays(_opts.CertValidityDays),
            serialNumber:     RandomSerial());

        var pem = ToPem(leaf) + ToPem(_caRoot);
        var certId = Guid.NewGuid().ToString("N");
        _certs[certId] = Encoding.UTF8.GetBytes(pem);

        var updated = order with
        {
            Status      = "valid",
            CertId      = certId,
            Csr         = finalize.Csr,
        };
        _orders[orderId] = updated;

        ctx.Response.Headers["Replay-Nonce"] = MintNonce();
        await WriteJson(ctx, 200, ToWire(updated, ctx));
        leaf.Dispose();
    }

    private async Task GetOrder(HttpContext ctx, string orderId)
    {
        try { _ = await ReadEnvelope(ctx); }
        catch (AcmeJwsException ex) { await WriteProblem(ctx, 400, "malformed", ex.Message); return; }

        if (!_orders.TryGetValue(orderId, out var order))
        {
            await WriteProblem(ctx, 404, "malformed", $"unknown order '{orderId}'."); return;
        }
        ctx.Response.Headers["Replay-Nonce"] = MintNonce();
        await WriteJson(ctx, 200, ToWire(order, ctx));
    }

    private async Task GetCert(HttpContext ctx, string certId)
    {
        try { _ = await ReadEnvelope(ctx); }
        catch (AcmeJwsException ex) { await WriteProblem(ctx, 400, "malformed", ex.Message); return; }

        if (!_certs.TryGetValue(certId, out var bytes))
        {
            await WriteProblem(ctx, 404, "malformed", $"unknown cert '{certId}'."); return;
        }
        ctx.Response.ContentType = AcmeWire.ContentTypePemCert;
        ctx.Response.Headers["Replay-Nonce"] = MintNonce();
        ctx.Response.StatusCode = 200;
        await ctx.Response.Body.WriteAsync(bytes);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private string MintNonce() =>
        NipSigner.Base64Url(BitConverter.GetBytes(Interlocked.Increment(ref _nonceCounter)));

    private string Url(HttpContext ctx, string sub) =>
        $"{ctx.Request.Scheme}://{ctx.Request.Host}{_opts.PathPrefix.TrimEnd('/')}{sub}";

    private static bool TryRouteResource(string sub, string prefix, out string id)
    {
        if (sub.StartsWith(prefix, StringComparison.Ordinal))
        {
            id = sub[prefix.Length..];
            return id.Length > 0 && !id.Contains('/');
        }
        id = string.Empty;
        return false;
    }

    private static async Task<AcmeJwsEnvelope> ReadEnvelope(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ms.Position = 0;
        var env = JsonSerializer.Deserialize<AcmeJwsEnvelope>(ms.ToArray())
            ?? throw new AcmeJwsException("body is not a JWS envelope.");
        return env;
    }

    private (AcmeProtectedHeader header, NSecPublicKey pubKey) ResolveJwk(AcmeJwsEnvelope envelope)
    {
        var headerJson = Encoding.UTF8.GetString(NipSigner.FromBase64Url(envelope.ProtectedHeader));
        var header = JsonSerializer.Deserialize<AcmeProtectedHeader>(headerJson)
            ?? throw new AcmeJwsException("protected header is not parseable.");
        if (header.Jwk is null)
            throw new AcmeJwsException("newAccount requires a jwk in the protected header.");
        return (header, AcmeJws.ImportJwk(header.Jwk));
    }

    private (AcmeProtectedHeader header, AccountState account) ResolveAccount(AcmeJwsEnvelope envelope)
    {
        var headerJson = Encoding.UTF8.GetString(NipSigner.FromBase64Url(envelope.ProtectedHeader));
        var header = JsonSerializer.Deserialize<AcmeProtectedHeader>(headerJson)
            ?? throw new AcmeJwsException("protected header is not parseable.");
        if (header.Kid is null)
            throw new AcmeJwsException("post-account requests require kid in the protected header.");

        var thumbprint = header.Kid.Split('/').Last();
        if (!_accounts.TryGetValue(thumbprint, out var account))
            throw new AcmeJwsException($"unknown account '{thumbprint}'.");
        return (header, account);
    }

    private static Task WriteJson(HttpContext ctx, int status, object value)
    {
        ctx.Response.StatusCode  = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(value, s_jsonOpts));
    }

    private static Task WriteProblem(HttpContext ctx, int status, string typeSlug, string detail)
    {
        ctx.Response.StatusCode  = status;
        ctx.Response.ContentType = AcmeWire.ContentTypeProblem;
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new AcmeProblemDetail
        {
            Type   = typeSlug,
            Detail = detail,
            Status = status,
        }));
    }

    private AcmeOrder ToWire(OrderState s, HttpContext ctx) => new()
    {
        Status         = s.Status,
        Identifiers    = new[] { s.Identifier },
        Authorizations = new[] { Url(ctx, $"/authz/{s.AuthzId}") },
        Finalize       = Url(ctx, $"/finalize/{s.Id}"),
        Certificate    = s.CertId is null ? null : Url(ctx, $"/cert/{s.CertId}"),
    };

    private AcmeAuthorization ToWire(AuthorizationState s, HttpContext ctx)
    {
        var challs = s.ChallengeIds.Select(id => _challenges[id])
            .Select(c => ToWire(c, ctx)).ToArray();
        return new AcmeAuthorization
        {
            Status     = s.Status,
            Identifier = s.Identifier,
            Challenges = challs,
        };
    }

    private AcmeChallenge ToWire(ChallengeState s, HttpContext ctx) => new()
    {
        Type      = s.Type,
        Url       = Url(ctx, $"/chall/{s.Id}"),
        Status    = s.Status,
        Token     = s.Token,
        Validated = s.Validated,
    };

    private static string? ExtractCn(X500DistinguishedName dn)
    {
        var formatted = dn.Format(false);
        const string p = "CN=";
        var idx = formatted.IndexOf(p, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var rest = formatted[(idx + p.Length)..];
        var commaIdx = rest.IndexOf(", ", StringComparison.Ordinal);
        return commaIdx >= 0 ? rest[..commaIdx] : rest;
    }

    private static string ToPem(X509Certificate2 cert)
    {
        var b64 = Convert.ToBase64String(cert.RawData, Base64FormattingOptions.InsertLineBreaks);
        return "-----BEGIN CERTIFICATE-----\n" + b64 + "\n-----END CERTIFICATE-----\n";
    }

    private static byte[] RandomSerial()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        bytes[0] &= 0x7F;
        if (bytes[0] == 0) bytes[0] = 0x01;
        return bytes;
    }

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ── In-memory state ─────────────────────────────────────────────────────

    private sealed record AccountState(string Thumbprint, AcmeJwk Jwk, string Status);

    private sealed record OrderState(
        string Id,
        string AccountThumbprint,
        AcmeIdentifier Identifier,
        string AuthzId,
        string Status,
        string? CertId,
        string? Csr);

    private sealed record AuthorizationState(
        string Id,
        string OrderId,
        AcmeIdentifier Identifier,
        string Status,
        IReadOnlyList<string> ChallengeIds);

    private sealed record ChallengeState(
        string Id,
        string Type,
        string Token,
        string Status,
        string AccountThumbprint,
        string AuthzId,
        string? Validated = null);
}

/// <summary>Configuration for <see cref="AcmeServer"/>.</summary>
public sealed class AcmeServerOptions
{
    /// <summary>HTTP path prefix for the server, e.g. <c>"/acme"</c>.</summary>
    public required string PathPrefix { get; init; }

    /// <summary>NID of the issuing CA, e.g. <c>"urn:nps:org:ca.example.com"</c>.</summary>
    public required string CaNid { get; init; }

    /// <summary>Validity period for issued leaf certs.</summary>
    public int CertValidityDays { get; init; } = 30;
}

/// <summary>RFC 8555 §7.4 newOrder payload.</summary>
public sealed record AcmeNewOrderPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("identifiers")]
    public required IReadOnlyList<AcmeIdentifier> Identifiers { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("notBefore")]
    public string? NotBefore { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("notAfter")]
    public string? NotAfter { get; init; }
}
