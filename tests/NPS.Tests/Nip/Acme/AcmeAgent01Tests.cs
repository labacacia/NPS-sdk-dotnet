// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NPS.NIP;
using NPS.NIP.Acme;
using NPS.NIP.Ca;
using NPS.NIP.Crypto;
using NPS.NIP.X509;
using NSec.Cryptography;

namespace NPS.Tests.Nip.Acme;

/// <summary>
/// End-to-end test for the NPS-RFC-0002 §4.4 ACME <c>agent-01</c>
/// challenge round trip. Hosts <see cref="AcmeServer"/> in-process under
/// TestServer and drives it with <see cref="AcmeClient"/>. The empirical
/// data this test produces feeds RFC §9.
/// </summary>
public sealed class AcmeAgent01Tests : IAsyncLifetime
{
    private const string CaNid       = "urn:nps:org:ca.acme-test.example";
    private const string AgentNid    = "urn:nps:agent:ca.acme-test.example:rfc0002-acme-001";
    private const string PathPrefix  = "/acme";

    private readonly string            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private NipKeyManager              _caKeys  = null!;
    private NipCaService               _caSvc   = null!;
    private X509Certificate2           _caRoot  = null!;
    private IHost                      _host    = null!;
    private HttpClient                 _http    = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);

        _caKeys = new NipKeyManager();
        _caKeys.Generate(Path.Combine(_tempDir, "ca.key.enc"), "testpass");

        var caOpts = new NipCaOptions
        {
            CaNid                 = CaNid,
            KeyFilePath           = Path.Combine(_tempDir, "ca.key.enc"),
            KeyPassphrase         = "testpass",
            BaseUrl               = "https://ca.acme-test.example",
            AgentCertValidityDays = 30,
            NodeCertValidityDays  = 90,
            RenewalWindowDays     = 7,
        };
        _caSvc = new NipCaService(caOpts, new InMemoryStore(), _caKeys);

        _caRoot = NipX509Builder.IssueRoot(
            CaNid, _caKeys.PrivateKey,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(5),
            RandomSerial());

        var serverOpts = new AcmeServerOptions
        {
            PathPrefix = PathPrefix, CaNid = CaNid, CertValidityDays = 30,
        };

        _host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton(serverOpts);
                    services.AddSingleton(_caSvc);
                    services.AddSingleton(_caKeys);
                    services.AddSingleton(_caRoot);
                    services.AddSingleton<AcmeServer>(sp => new AcmeServer(
                        sp.GetRequiredService<AcmeServerOptions>(),
                        sp.GetRequiredService<NipCaService>(),
                        sp.GetRequiredService<NipKeyManager>(),
                        sp.GetRequiredService<X509Certificate2>()));
                });
                web.Configure(app =>
                {
                    var server = app.ApplicationServices.GetRequiredService<AcmeServer>();
                    server.MapEndpoints(app);
                    app.Run(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
                });
            })
            .Build();
        await _host.StartAsync();
        _http = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        _caRoot.Dispose();
        _caKeys.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── TC: happy path round trip ───────────────────────────────────────────

    [Fact]
    public async Task IssueAgentCert_RoundTrip_ReturnsValidPemChain()
    {
        using var agentKey = NewAgentKey();
        var client         = new AcmeClient(_http, new Uri($"http://localhost{PathPrefix}/directory"), agentKey);

        var pem = await client.IssueAgentCertAsync(AgentNid);

        Assert.Contains("-----BEGIN CERTIFICATE-----", pem);
        Assert.NotNull(client.AccountUrl);

        // Verify the leaf cert: issuer = CA root, subject = agent NID, EKU = agent-identity.
        var (leafCert, rootCert) = ParsePemChain(pem);
        try
        {
            Assert.Contains("CN=" + AgentNid, leafCert.SubjectName.Format(false));
            Assert.Contains("CN=" + CaNid,    leafCert.IssuerName.Format(false));

            // The leaf was issued by our CA — chain it through NipX509Verifier to
            // double-check the same path the production verifier walks.
            var chainB64Url = new[]
            {
                Base64Url(leafCert.RawData),
                Base64Url(rootCert.RawData),
            };
            var result = NipX509Verifier.Verify(
                certChainBase64UrlDer: chainB64Url,
                assertedNid:           AgentNid,
                assertedAssuranceLevel: null,
                trustedRootCerts:      new[] { _caRoot });
            Assert.True(result.Valid,
                $"chain didn't verify: {result.ErrorCode} {result.Message}");
        }
        finally
        {
            leafCert.Dispose();
            rootCert.Dispose();
        }
    }

    // ── TC: bad agent-01 signature → NIP-ACME-CHALLENGE-FAILED ──────────────
    //
    // Drive the protocol up through GetAuthz manually, then post a forged
    // challenge response signed by a key that isn't the account's. Server
    // MUST reject with NIP-ACME-CHALLENGE-FAILED rather than issue a cert.

    [Fact]
    public async Task RespondAgent01_TamperedSignature_ServerReturnsChallengeFailed()
    {
        using var agentKey   = NewAgentKey();
        using var foreignKey = NewAgentKey();

        var dirResp = await _http.GetAsync($"{PathPrefix}/directory");
        dirResp.EnsureSuccessStatusCode();
        var directory = await dirResp.Content.ReadFromJsonAsync<AcmeDirectory>();
        Assert.NotNull(directory);

        // newAccount
        var nonce = await GetNonceAsync(directory!.NewNonce);
        var accountJwk = AcmeJws.Ed25519Jwk(agentKey.PublicKey);
        var accountEnv = AcmeJws.Sign(agentKey,
            new AcmeProtectedHeader { Nonce = nonce, Url = directory.NewAccount, Jwk = accountJwk },
            new AcmeNewAccountPayload { TermsOfServiceAgreed = true });
        var accountResp = await PostJwsAsync(directory.NewAccount, accountEnv);
        accountResp.EnsureSuccessStatusCode();
        var kid = accountResp.Headers.Location!.ToString();

        // newOrder
        nonce = await GetNonceAsync(directory.NewNonce);
        var orderEnv = AcmeJws.Sign(agentKey,
            new AcmeProtectedHeader { Nonce = nonce, Url = directory.NewOrder, Kid = kid },
            new AcmeNewOrderPayload
            {
                Identifiers = new[] { new AcmeIdentifier(AcmeWire.IdentifierTypeNid, AgentNid) },
            });
        var orderResp = await PostJwsAsync(directory.NewOrder, orderEnv);
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<AcmeOrder>();
        Assert.NotNull(order);

        // POST-as-GET authz
        nonce = await GetNonceAsync(directory.NewNonce);
        var authzEnv = AcmeJws.Sign(agentKey,
            new AcmeProtectedHeader { Nonce = nonce, Url = order!.Authorizations[0], Kid = kid },
            payload: null);
        var authzResp = await PostJwsAsync(order.Authorizations[0], authzEnv);
        authzResp.EnsureSuccessStatusCode();
        var authz = await authzResp.Content.ReadFromJsonAsync<AcmeAuthorization>();
        var challenge = authz!.Challenges.First(c => c.Type == AcmeWire.ChallengeAgent01);

        // Forge: sign the token with the WRONG key. Server side verifies
        // against the account's JWK, so this MUST be rejected.
        nonce = await GetNonceAsync(directory.NewNonce);
        var forgedSig = SignatureAlgorithm.Ed25519.Sign(
            foreignKey, System.Text.Encoding.ASCII.GetBytes(challenge.Token));
        var challEnv = AcmeJws.Sign(agentKey,
            new AcmeProtectedHeader { Nonce = nonce, Url = challenge.Url, Kid = kid },
            new AcmeChallengeRespondPayload { AgentSignature = Base64Url(forgedSig) });
        var challResp = await PostJwsAsync(challenge.Url, challEnv);

        Assert.False(challResp.IsSuccessStatusCode);
        var body = await challResp.Content.ReadAsStringAsync();
        Assert.Contains(NipErrorCodes.AcmeChallengeFailed, body);
    }

    private async Task<string> GetNonceAsync(string newNonceUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Head, newNonceUrl);
        var resp = await _http.SendAsync(req);
        return resp.Headers.GetValues("Replay-Nonce").First();
    }

    private async Task<HttpResponseMessage> PostJwsAsync(string url, AcmeJwsEnvelope env)
    {
        var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(env),
            System.Text.Encoding.UTF8, AcmeWire.ContentTypeJoseJson);
        return await _http.PostAsync(url, content);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Key NewAgentKey() =>
        Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });

    private static byte[] RandomSerial()
    {
        var b = new byte[16];
        RandomNumberGenerator.Fill(b);
        b[0] &= 0x7F;
        if (b[0] == 0) b[0] = 0x01;
        return b;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Pull the leaf and root certs out of a PEM concatenation ordered as
    /// leaf-then-root. Strict — the test fixture controls the producer side
    /// so we need only the simple shape.
    /// </summary>
    private static (X509Certificate2 leaf, X509Certificate2 root) ParsePemChain(string pem)
    {
        var marker = "-----BEGIN CERTIFICATE-----";
        var first  = pem.IndexOf(marker, StringComparison.Ordinal);
        var second = pem.IndexOf(marker, first + marker.Length, StringComparison.Ordinal);

        var leafPem = pem[first..second];
        var rootPem = pem[second..];

        return (
            X509Certificate2.CreateFromPem(leafPem),
            X509Certificate2.CreateFromPem(rootPem));
    }

    /// <summary>
    /// In-memory CA store sufficient for issuance tests. Mirrors the helper
    /// in <c>NipCaServiceTests</c> but kept private to avoid cross-test
    /// fixture coupling.
    /// </summary>
    private sealed class InMemoryStore : INipCaStore
    {
        private readonly List<NipCertRecord> _records = new();
        private long _serial;

        public Task SaveAsync(NipCertRecord record, CancellationToken ct = default)
        {
            _records.Add(record);
            return Task.CompletedTask;
        }
        public Task<NipCertRecord?> GetByNidAsync(string nid, CancellationToken ct = default) =>
            Task.FromResult<NipCertRecord?>(
                _records.Where(r => r.Nid == nid).MaxBy(r => r.IssuedAt));
        public Task<NipCertRecord?> GetBySerialAsync(string serial, CancellationToken ct = default) =>
            Task.FromResult<NipCertRecord?>(_records.FirstOrDefault(r => r.Serial == serial));
        public Task<bool> RevokeAsync(string nid, string reason, DateTime revokedAt, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<string> NextSerialAsync(CancellationToken ct = default) =>
            Task.FromResult($"0x{Interlocked.Increment(ref _serial):X}");
        public Task<IReadOnlyList<NipCertRecord>> GetRevokedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NipCertRecord>>(Array.Empty<NipCertRecord>());
        public Task<IReadOnlyList<NipCertRecord>> GetByParentNidAsync(string parentNid, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NipCertRecord>>(
                _records.Where(r => r.ParentNid == parentNid).ToList());
    }

}
