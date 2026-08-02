// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using NPS.NIP.Client;

namespace NPS.Tests.Nip;

public sealed class NipCaClientTests
{
    [Fact]
    public async Task RegisterAgent_SendsTypedRequestWithBearerToken()
    {
        var handler = new CaptureHandler(
            "{" +
            "\"frame\":\"0x20\",\"nid\":\"urn:nps:agent:ca.test:a\",\"pub_key\":\"ed25519:a\"," +
            "\"capabilities\":[],\"scope\":{},\"issued_by\":\"urn:nps:org:ca.test\"," +
            "\"issued_at\":\"2026-01-01T00:00:00Z\",\"expires_at\":\"2026-01-02T00:00:00Z\"," +
            "\"serial\":\"0x1\",\"signature\":\"ed25519:sig\"" +
            "}");
        var client = new NipCaClient(new HttpClient(handler) { BaseAddress = new Uri("https://ca.test") }, "/nip");

        var frame = await client.RegisterAgentAsync(
            new NipCaRegisterRequest("a", "ed25519:a", ["nwp:query"], "{}"),
            bearerToken: "secret");

        Assert.Equal("urn:nps:agent:ca.test:a", frame.Nid);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/nip/v1/agents/register", handler.LastRequest.RequestUri!.PathAndQuery);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("secret", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("\"identifier\":\"a\"", handler.LastBody);
    }

    [Fact]
    public async Task ErrorResponse_ThrowsTypedException()
    {
        var handler = new CaptureHandler(
            """{"error_code":"NIP-CA-UNAUTHORIZED","message":"nope"}""",
            HttpStatusCode.Unauthorized);
        var client = new NipCaClient(new HttpClient(handler) { BaseAddress = new Uri("https://ca.test") });

        var ex = await Assert.ThrowsAsync<NipCaClientException>(() =>
            client.RenewAgentAsync("urn:nps:agent:ca.test:a"));

        Assert.Equal("NIP-CA-UNAUTHORIZED", ex.ErrorCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task GetCertificates_SendsOperatorBearerToken()
    {
        var handler = new CaptureHandler(
            """{"entries":[{"nid":"urn:nps:agent:ca.test:a","entity_type":"agent","serial":"0x1","pub_key":"ed25519:a","capabilities":[],"scope":{},"issued_by":"urn:nps:org:ca.test","issued_at":"2026-01-01T00:00:00Z","expires_at":"2026-01-02T00:00:00Z"}]}""");
        var client = new NipCaClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://ca.test") },
            "/nip");

        var records = await client.GetCertificatesAsync("operator-secret");

        Assert.Single(records.Entries);
        Assert.Equal("/nip/v1/certificates", handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("operator-secret", handler.LastRequest.Headers.Authorization.Parameter);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = "";

        public CaptureHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
