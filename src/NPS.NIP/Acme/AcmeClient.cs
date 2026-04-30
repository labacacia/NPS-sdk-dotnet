// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using NPS.NIP.Crypto;
using NPS.NIP.X509;
using NSec.Cryptography;

namespace NPS.NIP.Acme;

/// <summary>
/// RFC 8555 ACME client extended with the
/// <c>agent-01</c> challenge type per NPS-RFC-0002 §4.4.
///
/// <para>The client is intentionally minimal — it covers exactly the flow
/// the prototype's empirical tests need: <c>newAccount</c> →
/// <c>newOrder</c> → fetch authz → respond to <c>agent-01</c> challenge →
/// finalize with a CSR → fetch the issued PEM cert chain. No
/// HTTP-01 / DNS-01 support; no account key rotation; no revocation.</para>
///
/// <para>The same Ed25519 key is used for both the ACME account JWS and the
/// CSR public key. RFC §4.4 keeps these conceptually separable but the
/// prototype collapses them — extending to two-key issuance is out of
/// scope. Extending the client later to use distinct keys requires only
/// changing how the CSR is built.</para>
/// </summary>
public sealed class AcmeClient
{
    private readonly HttpClient _http;
    private readonly Uri        _directoryUrl;
    private readonly Key        _accountKey;
    private readonly AcmeJwk    _accountJwk;

    private AcmeDirectory? _directory;
    private string?        _accountUrl;

    public AcmeClient(HttpClient http, Uri directoryUrl, Key accountKey)
    {
        _http         = http;
        _directoryUrl = directoryUrl;
        _accountKey   = accountKey;
        _accountJwk   = AcmeJws.Ed25519Jwk(accountKey.PublicKey);
    }

    /// <summary>The Account URL returned by <c>newAccount</c>; null until issued.</summary>
    public string? AccountUrl => _accountUrl;

    /// <summary>
    /// Run the full <c>agent-01</c> issuance flow for <paramref name="nid"/>.
    /// Returns the issued PEM cert chain (leaf + root, in that order).
    /// </summary>
    public async Task<string> IssueAgentCertAsync(string nid, CancellationToken ct = default)
    {
        await EnsureDirectoryAsync(ct);
        await NewAccountAsync(ct);

        var (orderUrl, order) = await NewOrderAsync(nid, ct);
        var authzUrl = order.Authorizations[0];
        var authz    = await FetchAuthzAsync(authzUrl, ct);

        var challenge = authz.Challenges.FirstOrDefault(c => c.Type == AcmeWire.ChallengeAgent01)
            ?? throw new AcmeClientException(
                $"server did not offer an agent-01 challenge; got types: " +
                string.Join(",", authz.Challenges.Select(c => c.Type)));

        await RespondAgent01Async(challenge, ct);

        // Server flips the order to "ready"; finalize with a CSR.
        var csr = BuildCsr(nid);
        var finalized = await FinalizeAsync(order.Finalize, csr, ct);
        if (finalized.Certificate is null)
            throw new AcmeClientException(
                $"finalize did not produce a certificate; order status='{finalized.Status}'.");

        return await DownloadPemAsync(finalized.Certificate, ct);
    }

    // ── Stages ──────────────────────────────────────────────────────────────

    private async Task EnsureDirectoryAsync(CancellationToken ct)
    {
        if (_directory is not null) return;
        var resp = await _http.GetAsync(_directoryUrl, ct);
        await EnsureSuccessAsync(resp, "directory");
        _directory = await resp.Content.ReadFromJsonAsync<AcmeDirectory>(s_json, ct)
            ?? throw new AcmeClientException("directory response did not parse.");
    }

    private async Task NewAccountAsync(CancellationToken ct)
    {
        if (_accountUrl is not null) return;

        var nonce = await GetNonceAsync(ct);
        var header = new AcmeProtectedHeader
        {
            Nonce = nonce, Url = _directory!.NewAccount, Jwk = _accountJwk,
        };
        var envelope = AcmeJws.Sign(_accountKey, header,
            new AcmeNewAccountPayload { TermsOfServiceAgreed = true });

        var resp = await PostJwsAsync(_directory.NewAccount, envelope, ct);
        await EnsureSuccessAsync(resp, "new-account");
        _accountUrl = resp.Headers.Location?.ToString()
            ?? throw new AcmeClientException("new-account response missing Location header.");
    }

    private async Task<(string orderUrl, AcmeOrder order)> NewOrderAsync(string nid, CancellationToken ct)
    {
        var nonce = await GetNonceAsync(ct);
        var header = new AcmeProtectedHeader
        {
            Nonce = nonce, Url = _directory!.NewOrder, Kid = _accountUrl,
        };
        var envelope = AcmeJws.Sign(_accountKey, header, new AcmeNewOrderPayload
        {
            Identifiers = new[] { new AcmeIdentifier(AcmeWire.IdentifierTypeNid, nid) },
        });

        var resp = await PostJwsAsync(_directory.NewOrder, envelope, ct);
        await EnsureSuccessAsync(resp, "new-order");
        var order = await resp.Content.ReadFromJsonAsync<AcmeOrder>(s_json, ct)
            ?? throw new AcmeClientException("new-order response did not parse.");
        var orderUrl = resp.Headers.Location?.ToString() ?? "";
        return (orderUrl, order);
    }

    private async Task<AcmeAuthorization> FetchAuthzAsync(string authzUrl, CancellationToken ct)
    {
        var resp = await PostAsGetAsync(authzUrl, ct);
        await EnsureSuccessAsync(resp, "authz");
        return await resp.Content.ReadFromJsonAsync<AcmeAuthorization>(s_json, ct)
            ?? throw new AcmeClientException("authz response did not parse.");
    }

    private async Task RespondAgent01Async(AcmeChallenge challenge, CancellationToken ct)
    {
        // The agent-01 client proof: Ed25519 signature over the token bytes.
        var tokenBytes = Encoding.ASCII.GetBytes(challenge.Token);
        var sig        = SignatureAlgorithm.Ed25519.Sign(_accountKey, tokenBytes);

        var nonce = await GetNonceAsync(ct);
        var header = new AcmeProtectedHeader
        {
            Nonce = nonce, Url = challenge.Url, Kid = _accountUrl,
        };
        var envelope = AcmeJws.Sign(_accountKey, header, new AcmeChallengeRespondPayload
        {
            AgentSignature = NipSigner.Base64Url(sig),
        });

        var resp = await PostJwsAsync(challenge.Url, envelope, ct);
        await EnsureSuccessAsync(resp, "challenge-respond");
    }

    private async Task<AcmeOrder> FinalizeAsync(string finalizeUrl, byte[] csrDer, CancellationToken ct)
    {
        var nonce = await GetNonceAsync(ct);
        var header = new AcmeProtectedHeader
        {
            Nonce = nonce, Url = finalizeUrl, Kid = _accountUrl,
        };
        var envelope = AcmeJws.Sign(_accountKey, header, new AcmeFinalizePayload(
            Csr: NipSigner.Base64Url(csrDer)));

        var resp = await PostJwsAsync(finalizeUrl, envelope, ct);
        await EnsureSuccessAsync(resp, "finalize");
        return await resp.Content.ReadFromJsonAsync<AcmeOrder>(s_json, ct)
            ?? throw new AcmeClientException("finalize response did not parse.");
    }

    private async Task<string> DownloadPemAsync(string certUrl, CancellationToken ct)
    {
        var resp = await PostAsGetAsync(certUrl, ct);
        await EnsureSuccessAsync(resp, "cert-download");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ── HTTP ────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostJwsAsync(string url, AcmeJwsEnvelope envelope, CancellationToken ct)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(envelope, s_json),
            Encoding.UTF8, AcmeWire.ContentTypeJoseJson);
        return await _http.PostAsync(url, content, ct);
    }

    private async Task<HttpResponseMessage> PostAsGetAsync(string url, CancellationToken ct)
    {
        // RFC 8555 §6.3 — GET-as-POST with empty payload, signed by the account.
        var nonce = await GetNonceAsync(ct);
        var header = new AcmeProtectedHeader { Nonce = nonce, Url = url, Kid = _accountUrl };
        var envelope = AcmeJws.Sign(_accountKey, header, payload: null);
        return await PostJwsAsync(url, envelope, ct);
    }

    private async Task<string> GetNonceAsync(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Head, _directory!.NewNonce);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.Headers.TryGetValues("Replay-Nonce", out var values))
            throw new AcmeClientException("server did not return a Replay-Nonce header.");
        return values.First();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string stage)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        throw new AcmeClientException(
            $"{stage} returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
    }

    // ── CSR ─────────────────────────────────────────────────────────────────

    private byte[] BuildCsr(string nid)
    {
        var subjectName  = new X500DistinguishedName($"CN={nid}");
        var subjectPub   = Ed25519PublicKey.FromRaw(_accountKey.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var req          = new CertificateRequest(subjectName, subjectPub, HashAlgorithmName.SHA256);

        // SAN URI = NID — same constraint the verifier enforces (RFC-0002 §4.1).
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddUri(new Uri(nid, UriKind.Absolute));
        req.CertificateExtensions.Add(sanBuilder.Build(critical: false));

        var generator = new Ed25519X509SignatureGenerator(_accountKey);
        return req.CreateSigningRequest(generator);
    }

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Surfaces an ACME client failure to the caller.</summary>
public sealed class AcmeClientException : Exception
{
    public AcmeClientException(string message) : base(message) { }
}
