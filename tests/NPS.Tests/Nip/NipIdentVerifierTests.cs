// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using NSec.Cryptography;
using NPS.NIP.Ca;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;
using NPS.NIP.Verification;

namespace NPS.Tests.Nip;

/// <summary>
/// Unit tests for <see cref="NipIdentVerifier"/> covering all six NPS-3 §7 steps
/// and the <see cref="NipIdentVerifier.NwpPathMatches"/> scope helper.
/// </summary>
public sealed class NipIdentVerifierTests : IDisposable
{
    // ── Shared CA fixture ─────────────────────────────────────────────────────

    private const string CaNid    = "urn:nps:org:ca.verifier-test.example";
    private const string AgentNid = "urn:nps:agent:ca.verifier-test.example:agent-001";
    private const string Serial   = "0xABC001";

    private readonly string       _tempDir;
    private readonly NipKeyManager _caKeys;
    private readonly string       _caPubKeyEncoded;
    private readonly NipVerifierOptions _defaultOpts;

    public NipIdentVerifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _caKeys = new NipKeyManager();
        _caKeys.Generate(Path.Combine(_tempDir, "ca.key.enc"), "testpass");

        _caPubKeyEncoded = NipSigner.EncodePublicKey(_caKeys.PublicKey);

        _defaultOpts = new NipVerifierOptions
        {
            TrustedIssuers = new Dictionary<string, string>
            {
                [CaNid] = _caPubKeyEncoded,
            },
        };
    }

    public void Dispose()
    {
        _caKeys.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds and signs a valid <see cref="IdentFrame"/> whose scope covers
    /// <c>nwp://api.test.example/*</c>. All fields are well-formed.
    /// </summary>
    private IdentFrame MakeFrame(
        DateTime? expiresAt   = null,
        string?   issuedBy    = null,
        string[]? caps        = null,
        string?   scopeJson   = null,
        string?   serial      = null)
    {
        var now       = DateTime.UtcNow;
        var exp       = (expiresAt ?? now.AddDays(1)).ToString("O");
        var iat       = now.ToString("O");
        var iss       = issuedBy ?? CaNid;
        var serialVal = serial   ?? Serial;
        var capsList  = (IReadOnlyList<string>)(caps ?? ["nwp:query", "nwp:stream"]);
        var scope     = JsonDocument.Parse(
            scopeJson ?? """{"nodes":["nwp://api.test.example/*"]}""").RootElement;

        // Build payload in the same alphabetical order as IssueFrame
        var payload = new
        {
            capabilities = capsList,
            expires_at   = exp,
            frame        = "0x20",
            issued_at    = iat,
            issued_by    = iss,
            nid          = AgentNid,
            pub_key      = "ed25519:placeholder",
            scope,
            serial       = serialVal,
        };
        var signature = NipSigner.Sign(_caKeys.PrivateKey, payload);

        return new IdentFrame
        {
            Nid          = AgentNid,
            PubKey       = "ed25519:placeholder",
            Capabilities = capsList,
            Scope        = scope.Clone(),
            IssuedBy     = iss,
            IssuedAt     = iat,
            ExpiresAt    = exp,
            Serial       = serialVal,
            Signature    = signature,
        };
    }

    private static NipIdentVerifier MakeVerifier(
        NipVerifierOptions?    opts    = null,
        IHttpClientFactory?    factory = null) =>
        new(opts ?? new NipVerifierOptions { TrustedIssuers = new() }, factory);

    // ── Step 1: Expiry ────────────────────────────────────────────────────────

    [Fact]
    public async Task Step1_ExpiredFrame_Fails()
    {
        var frame   = MakeFrame(expiresAt: DateTime.UtcNow.AddDays(-1));
        var verifier = MakeVerifier(_defaultOpts);

        var result = await verifier.VerifyAsync(frame,
            new NipVerifyContext { AsOf = DateTime.UtcNow });

        Assert.False(result.IsValid);
        Assert.Equal(1, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertExpired, result.ErrorCode);
    }

    [Fact]
    public async Task Step1_InvalidExpiresAt_Fails()
    {
        // Build a frame with a bad date string by re-creating the IdentFrame manually
        var frame = MakeFrame() with { ExpiresAt = "not-a-date" };
        var verifier = MakeVerifier(_defaultOpts);

        var result = await verifier.VerifyAsync(frame);

        Assert.False(result.IsValid);
        Assert.Equal(1, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertExpired, result.ErrorCode);
    }

    [Fact]
    public async Task Step1_FutureFrame_Passes()
    {
        var frame    = MakeFrame(expiresAt: DateTime.UtcNow.AddDays(30));
        var verifier = MakeVerifier(_defaultOpts);

        // Only checking expiry — scope check will pass because AsOf is set and path is null
        var result = await verifier.VerifyAsync(frame,
            new NipVerifyContext { AsOf = DateTime.UtcNow });

        // If it fails, it should NOT be at step 1
        if (!result.IsValid)
            Assert.NotEqual(1, result.FailedStep);
    }

    // ── Step 2: Trusted issuer ────────────────────────────────────────────────

    [Fact]
    public async Task Step2_UnknownIssuer_Fails()
    {
        var frame    = MakeFrame(issuedBy: "urn:nps:org:unknown.ca");
        var verifier = MakeVerifier(_defaultOpts);

        var result = await verifier.VerifyAsync(frame);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertUntrusted, result.ErrorCode);
    }

    [Fact]
    public async Task Step2_EmptyTrustedIssuers_Fails()
    {
        var frame    = MakeFrame();
        var opts     = new NipVerifierOptions { TrustedIssuers = new() };
        var verifier = MakeVerifier(opts);

        var result = await verifier.VerifyAsync(frame);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.FailedStep);
    }

    // ── Step 3: Signature ─────────────────────────────────────────────────────

    [Fact]
    public async Task Step3_InvalidPublicKeyEncoding_Fails()
    {
        var frame    = MakeFrame();
        var opts     = new NipVerifierOptions
        {
            TrustedIssuers = new Dictionary<string, string>
            {
                [CaNid] = "ed25519:!!!notvalidbase64!!!",
            },
        };
        var verifier = MakeVerifier(opts);

        var result = await verifier.VerifyAsync(frame);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertSigInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task Step3_TamperedFrame_Fails()
    {
        var frame = MakeFrame() with
        {
            // Change a field after signing → signature no longer matches
            Serial = "0xDEADBEEF",
        };
        var verifier = MakeVerifier(_defaultOpts);

        var result = await verifier.VerifyAsync(frame);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertSigInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task Step3_WrongSigningKey_Fails()
    {
        var frame = MakeFrame();
        // Register the CA NID but with a DIFFERENT (wrong) public key
        using var wrongKey = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var opts = new NipVerifierOptions
        {
            TrustedIssuers = new Dictionary<string, string>
            {
                [CaNid] = NipSigner.EncodePublicKey(wrongKey.PublicKey),
            },
        };
        var verifier = MakeVerifier(opts);

        var result = await verifier.VerifyAsync(frame);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertSigInvalid, result.ErrorCode);
    }

    // ── Step 4: Revocation (local CRL) ───────────────────────────────────────

    [Fact]
    public async Task Step4_LocalCrl_RevokedSerial_Fails()
    {
        var frame = MakeFrame();
        var opts  = new NipVerifierOptions
        {
            TrustedIssuers      = _defaultOpts.TrustedIssuers,
            LocalRevokedSerials = new HashSet<string> { Serial },
        };
        var verifier = MakeVerifier(opts);

        var result = await verifier.VerifyAsync(frame);

        Assert.False(result.IsValid);
        Assert.Equal(4, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertRevoked, result.ErrorCode);
    }

    [Fact]
    public async Task Step4_LocalCrl_OtherSerial_Passes()
    {
        var frame = MakeFrame();
        var opts  = new NipVerifierOptions
        {
            TrustedIssuers      = _defaultOpts.TrustedIssuers,
            LocalRevokedSerials = new HashSet<string> { "0xFFFFFF" },
        };
        var verifier = MakeVerifier(opts);

        var result = await verifier.VerifyAsync(frame);

        // Should not fail at step 4
        if (!result.IsValid)
            Assert.NotEqual(4, result.FailedStep);
    }

    // ── Step 4: Revocation (OCSP) ─────────────────────────────────────────────

    [Fact]
    public async Task Step4_Ocsp_ValidResponse_Passes()
    {
        var frame   = MakeFrame();
        var factory = new StubHttpClientFactory(HttpStatusCode.OK, """{"valid":true}""");
        var opts    = new NipVerifierOptions
        {
            TrustedIssuers = _defaultOpts.TrustedIssuers,
            OcspUrl        = "https://ocsp.test.example/nip",
        };
        var verifier = MakeVerifier(opts, factory);

        var result = await verifier.VerifyAsync(frame);

        if (!result.IsValid)
            Assert.NotEqual(4, result.FailedStep);
    }

    [Fact]
    public async Task Step4_Ocsp_RevokedResponse_Fails()
    {
        var frame   = MakeFrame();
        var factory = new StubHttpClientFactory(HttpStatusCode.OK,
            """{"valid":false,"error_code":"NIP-CERT-REVOKED"}""");
        var opts    = new NipVerifierOptions
        {
            TrustedIssuers = _defaultOpts.TrustedIssuers,
            OcspUrl        = "https://ocsp.test.example/nip",
        };
        var verifier = MakeVerifier(opts, factory);

        var result = await verifier.VerifyAsync(frame);

        Assert.False(result.IsValid);
        Assert.Equal(4, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertRevoked, result.ErrorCode);
    }

    [Fact]
    public async Task Step4_Ocsp_NonSuccessStatus_Fails()
    {
        var frame   = MakeFrame();
        var factory = new StubHttpClientFactory(HttpStatusCode.ServiceUnavailable, "");
        var opts    = new NipVerifierOptions
        {
            TrustedIssuers = _defaultOpts.TrustedIssuers,
            OcspUrl        = "https://ocsp.test.example/nip",
        };
        var verifier = MakeVerifier(opts, factory);

        var result = await verifier.VerifyAsync(frame);

        Assert.False(result.IsValid);
        Assert.Equal(4, result.FailedStep);
        Assert.Equal(NipErrorCodes.OcspUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task Step4_Ocsp_NetworkError_FailsOpen()
    {
        var frame   = MakeFrame();
        var factory = new ThrowingHttpClientFactory();
        var opts    = new NipVerifierOptions
        {
            TrustedIssuers = _defaultOpts.TrustedIssuers,
            OcspUrl        = "https://ocsp.test.example/nip",
        };
        var verifier = MakeVerifier(opts, factory);

        // RFC 6960 §2.4 fail-open: network error → treat as not revoked
        var result = await verifier.VerifyAsync(frame);

        if (!result.IsValid)
            Assert.NotEqual(4, result.FailedStep);
    }

    // ── Step 5: Capabilities ──────────────────────────────────────────────────

    [Fact]
    public async Task Step5_MissingRequiredCapability_Fails()
    {
        var frame    = MakeFrame(caps: ["nwp:query"]);
        var verifier = MakeVerifier(_defaultOpts);
        var ctx      = new NipVerifyContext
        {
            RequiredCapabilities = ["nwp:query", "nwp:write"],
        };

        var result = await verifier.VerifyAsync(frame, ctx);

        Assert.False(result.IsValid);
        Assert.Equal(5, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertCapMissing, result.ErrorCode);
        Assert.Contains("nwp:write", result.Message!);
    }

    [Fact]
    public async Task Step5_AllRequiredCapabilitiesPresent_Passes()
    {
        var frame    = MakeFrame(caps: ["nwp:query", "nwp:stream", "nwp:write"]);
        var verifier = MakeVerifier(_defaultOpts);
        var ctx      = new NipVerifyContext
        {
            RequiredCapabilities = ["nwp:query", "nwp:write"],
        };

        var result = await verifier.VerifyAsync(frame, ctx);

        if (!result.IsValid)
            Assert.NotEqual(5, result.FailedStep);
    }

    [Fact]
    public async Task Step5_NoRequiredCapabilities_Skips()
    {
        var frame    = MakeFrame(caps: []);
        var verifier = MakeVerifier(_defaultOpts);

        // RequiredCapabilities is null → step 5 should be skipped
        var result = await verifier.VerifyAsync(frame);

        if (!result.IsValid)
            Assert.NotEqual(5, result.FailedStep);
    }

    // ── Step 6: Scope ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Step6_TargetPathCoveredByWildcard_Passes()
    {
        var frame    = MakeFrame(scopeJson: """{"nodes":["nwp://api.test.example/*"]}""");
        var verifier = MakeVerifier(_defaultOpts);
        var ctx      = new NipVerifyContext
        {
            TargetNodePath = "nwp://api.test.example/products",
        };

        var result = await verifier.VerifyAsync(frame, ctx);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Step6_TargetPathNotCovered_Fails()
    {
        var frame    = MakeFrame(scopeJson: """{"nodes":["nwp://api.test.example/*"]}""");
        var verifier = MakeVerifier(_defaultOpts);
        var ctx      = new NipVerifyContext
        {
            TargetNodePath = "nwp://other.domain.example/data",
        };

        var result = await verifier.VerifyAsync(frame, ctx);

        Assert.False(result.IsValid);
        Assert.Equal(6, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertScope, result.ErrorCode);
    }

    [Fact]
    public async Task Step6_MissingNodesField_Fails()
    {
        var frame    = MakeFrame(scopeJson: """{"actions":["read"]}""");
        var verifier = MakeVerifier(_defaultOpts);
        var ctx      = new NipVerifyContext
        {
            TargetNodePath = "nwp://api.test.example/products",
        };

        var result = await verifier.VerifyAsync(frame, ctx);

        Assert.False(result.IsValid);
        Assert.Equal(6, result.FailedStep);
        Assert.Equal(NipErrorCodes.CertScope, result.ErrorCode);
    }

    [Fact]
    public async Task Step6_NullTargetPath_Skips()
    {
        var frame    = MakeFrame(scopeJson: """{"nodes":[]}""");
        var verifier = MakeVerifier(_defaultOpts);

        // TargetNodePath is null → scope check skipped
        var result = await verifier.VerifyAsync(frame);

        Assert.True(result.IsValid);
    }

    // ── Full happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task FullVerify_ValidFrame_Succeeds()
    {
        var frame    = MakeFrame(caps: ["nwp:query", "nwp:stream"]);
        var verifier = MakeVerifier(_defaultOpts);
        var ctx      = new NipVerifyContext
        {
            RequiredCapabilities = ["nwp:query"],
            TargetNodePath       = "nwp://api.test.example/products",
        };

        var result = await verifier.VerifyAsync(frame, ctx);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.FailedStep);
        Assert.Null(result.ErrorCode);
    }

    // ── NwpPathMatches ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("*", "nwp://anything.example/path", true)]
    [InlineData("*", "", true)]
    [InlineData("nwp://api.example.com/*", "nwp://api.example.com/products", true)]
    [InlineData("nwp://api.example.com/*", "nwp://api.example.com/orders/123", true)]
    [InlineData("nwp://api.example.com/*", "nwp://api.example.com", true)]  // exact prefix
    [InlineData("nwp://api.example.com/*", "nwp://api.example.com/", true)]
    [InlineData("nwp://api.example.com/*", "nwp://other.example.com/products", false)]
    [InlineData("nwp://api.example.com/exact", "nwp://api.example.com/exact", true)]
    [InlineData("nwp://api.example.com/exact", "nwp://api.example.com/exact/sub", false)]
    [InlineData("nwp://api.example.com/exact", "nwp://API.EXAMPLE.COM/EXACT", true)] // case-insensitive
    [InlineData("nwp://api.example.com/*", "nwp://API.EXAMPLE.COM/data", true)]      // case-insensitive prefix
    [InlineData("nwp://api.example.com/prefix*", "nwp://api.example.com/prefixstuff", false)] // no mid-string wildcard
    public void NwpPathMatches_ReturnsExpected(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, NipIdentVerifier.NwpPathMatches(pattern, path));
    }
}

// ── HTTP stub helpers ─────────────────────────────────────────────────────────

file sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpStatusCode _status;
    private readonly string         _body;

    public StubHttpClientFactory(HttpStatusCode status, string body)
    {
        _status = status;
        _body   = body;
    }

    public HttpClient CreateClient(string name)
    {
        var handler  = new StubHandler(_status, _body);
        return new HttpClient(handler);
    }
}

file sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string         _body;

    public StubHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body   = body;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body,
                System.Text.Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}

file sealed class ThrowingHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(new ThrowingHandler());
}

file sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("Simulated network failure");
}
