// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.NIP.Frames;

namespace NPS.NIP.Client;

/// <summary>
/// Typed HTTP client for the NIP CA REST API (NPS-3 §8).
/// The caller owns the supplied <see cref="HttpClient"/> and its transport policy.
/// </summary>
public sealed class NipCaClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _prefix;

    public NipCaClient(HttpClient http, string routePrefix = "")
    {
        _http = http;
        _prefix = routePrefix.TrimEnd('/');
    }

    public Task<NipCaDiscoveryDocument> GetDiscoveryAsync(CancellationToken ct = default) =>
        GetJsonAsync<NipCaDiscoveryDocument>("/.well-known/nps-ca", ct);

    public Task<NipCaCrl> GetCrlAsync(CancellationToken ct = default) =>
        GetJsonAsync<NipCaCrl>($"{_prefix}/v1/crl", ct);

    public Task<IdentFrame> RegisterAgentAsync(
        NipCaRegisterRequest request, string? bearerToken = null, CancellationToken ct = default) =>
        SendJsonAsync<IdentFrame>(HttpMethod.Post, $"{_prefix}/v1/agents/register", request, bearerToken, ct);

    public Task<IdentFrame> RegisterNodeAsync(
        NipCaRegisterRequest request, string? bearerToken = null, CancellationToken ct = default) =>
        SendJsonAsync<IdentFrame>(HttpMethod.Post, $"{_prefix}/v1/nodes/register", request, bearerToken, ct);

    public Task<IdentFrame> RegisterAgentX509Async(
        NipCaRegisterX509Request request, string? bearerToken = null, CancellationToken ct = default) =>
        SendJsonAsync<IdentFrame>(HttpMethod.Post, $"{_prefix}/v1/agents/register-x509", request, bearerToken, ct);

    public Task<IdentFrame> RegisterNodeX509Async(
        NipCaRegisterX509Request request, string? bearerToken = null, CancellationToken ct = default) =>
        SendJsonAsync<IdentFrame>(HttpMethod.Post, $"{_prefix}/v1/nodes/register-x509", request, bearerToken, ct);

    public Task<IdentFrame> RenewAgentAsync(string nid, string? bearerToken = null, CancellationToken ct = default) =>
        SendJsonAsync<IdentFrame>(HttpMethod.Post, $"{_prefix}/v1/agents/{Uri.EscapeDataString(nid)}/renew", null, bearerToken, ct);

    public Task<IdentFrame> RenewNodeAsync(string nid, string? bearerToken = null, CancellationToken ct = default) =>
        SendJsonAsync<IdentFrame>(HttpMethod.Post, $"{_prefix}/v1/nodes/{Uri.EscapeDataString(nid)}/renew", null, bearerToken, ct);

    public Task<RevokeFrame> RevokeAgentAsync(
        string nid, string reason = "cessation_of_operation", string? bearerToken = null, CancellationToken ct = default) =>
        SendJsonAsync<RevokeFrame>(HttpMethod.Post, $"{_prefix}/v1/agents/{Uri.EscapeDataString(nid)}/revoke",
            new NipCaRevokeRequest(reason), bearerToken, ct);

    public Task<RevokeFrame> RevokeNodeAsync(
        string nid, string reason = "cessation_of_operation", string? bearerToken = null, CancellationToken ct = default) =>
        SendJsonAsync<RevokeFrame>(HttpMethod.Post, $"{_prefix}/v1/nodes/{Uri.EscapeDataString(nid)}/revoke",
            new NipCaRevokeRequest(reason), bearerToken, ct);

    public Task<NipCaVerifyResponse> VerifyAgentAsync(string nid, CancellationToken ct = default) =>
        GetJsonAsync<NipCaVerifyResponse>($"{_prefix}/v1/agents/{Uri.EscapeDataString(nid)}/verify", ct);

    public Task<NipCaVerifyResponse> VerifyNodeAsync(string nid, CancellationToken ct = default) =>
        GetJsonAsync<NipCaVerifyResponse>($"{_prefix}/v1/nodes/{Uri.EscapeDataString(nid)}/verify", ct);

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        return await ReadResponseAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string url,
        object? body,
        string? bearerToken,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOpts);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new("Bearer", bearerToken);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadResponseAsync<T>(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return (await response.Content.ReadFromJsonAsync<T>(JsonOpts, ct).ConfigureAwait(false))
                   ?? throw new NipCaClientException("NIP-CA-EMPTY-RESPONSE", "NIP CA returned an empty response.");

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var err = JsonSerializer.Deserialize<NipCaErrorResponse>(body, JsonOpts);
            throw new NipCaClientException(
                err?.ErrorCode ?? "NIP-CA-HTTP-ERROR",
                err?.Message ?? $"NIP CA returned HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        }
        catch (JsonException)
        {
            throw new NipCaClientException(
                "NIP-CA-HTTP-ERROR",
                $"NIP CA returned HTTP {(int)response.StatusCode}: {body}",
                response.StatusCode);
        }
    }
}

public sealed record NipCaRegisterRequest(
    string Identifier,
    string PubKey,
    IReadOnlyList<string>? Capabilities = null,
    string? ScopeJson = null,
    string? MetadataJson = null);

public sealed record NipCaRegisterX509Request(
    string Identifier,
    string PubKey,
    IReadOnlyList<string>? Capabilities = null,
    string? ScopeJson = null,
    string? MetadataJson = null,
    string? AssuranceLevel = null);

public sealed record NipCaRevokeRequest(string Reason);

public sealed record NipCaDiscoveryDocument
{
    public required string NpsCa { get; init; }
    public required string Issuer { get; init; }
    public string? DisplayName { get; init; }
    public required string PublicKey { get; init; }
    public IReadOnlyList<string>? Algorithms { get; init; }
    public JsonElement Endpoints { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
    public int MaxCertValidityDays { get; init; }
}

public sealed record NipCaCrl
{
    public required string IssuedBy { get; init; }
    public required string IssuedAt { get; init; }
    public required IReadOnlyList<NipCaCrlEntry> Entries { get; init; }
    public required string Signature { get; init; }
}

public sealed record NipCaCrlEntry
{
    public required string Nid { get; init; }
    public required string Serial { get; init; }
    public string? RevokedAt { get; init; }
    public string? Reason { get; init; }
}

public sealed record NipCaVerifyResponse
{
    public required bool Valid { get; init; }
    public string? Nid { get; init; }
    public string? ExpiresAt { get; init; }
    public string? Serial { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}

internal sealed record NipCaErrorResponse
{
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}

public sealed class NipCaClientException : Exception
{
    public string ErrorCode { get; }
    public System.Net.HttpStatusCode? StatusCode { get; }

    public NipCaClientException(string errorCode, string message, System.Net.HttpStatusCode? statusCode = null)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}
