// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.NIP.Reputation;

/// <summary>
/// HTTP client for the NPS-RFC-0004 reputation-log operator API (Phase 2).
///
/// <para>Endpoints used:</para>
/// <list type="bullet">
///   <item><c>POST  /v1/log/entries</c> — submit a signed entry.</item>
///   <item><c>GET   /v1/log/entries?nid=&amp;since=</c> — query entries.</item>
///   <item><c>GET   /v1/log/sth</c> — current Signed Tree Head.</item>
///   <item><c>GET   /v1/log/proof?seq=</c> — inclusion proof.</item>
///   <item><c>GET   /v1/log/gossip/sth</c> — gossip STH (Phase 3).</item>
/// </list>
///
/// <para>Use <see cref="VerifyInclusion"/> to locally verify that an entry
/// is committed in a given STH without trusting the server.</para>
/// </summary>
public sealed class ReputationLogClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool       _ownsHttp;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Build a client that owns an internal <see cref="HttpClient"/> pointed at
    /// <paramref name="baseUrl"/>. Dispose the client to release it.
    /// </summary>
    public ReputationLogClient(string baseUrl)
    {
        _http      = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + '/') };
        _ownsHttp  = true;
    }

    /// <summary>
    /// Build a client over a caller-owned <see cref="HttpClient"/>.
    /// <see cref="HttpClient.BaseAddress"/> must point at the log operator host.
    /// </summary>
    public ReputationLogClient(HttpClient http)
    {
        _http     = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttp = false;
    }

    // ── API methods ───────────────────────────────────────────────────────────

    /// <summary>
    /// Submit a signed entry to the log. Returns the entry as echoed back by
    /// the operator (with <c>seq</c>, <c>timestamp</c>, and <c>log_id</c>
    /// populated).
    /// </summary>
    public async Task<ReputationLogEntry> SubmitAsync(
        ReputationLogEntry entry,
        CancellationToken  ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var resp = await _http.PostAsJsonAsync("v1/log/entries", entry, s_json, ct);
        if (!resp.IsSuccessStatusCode)
            throw await BuildErrorAsync(resp, ct);

        return await resp.Content.ReadFromJsonAsync<ReputationLogEntry>(s_json, ct)
            ?? throw new InvalidOperationException("Log operator returned an empty entry.");
    }

    /// <summary>
    /// Query entries from the log. Optionally filter by subject NID and/or
    /// sequence number floor.
    /// </summary>
    public async Task<IReadOnlyList<ReputationLogEntry>> QueryAsync(
        string?           nid      = null,
        ulong?            sinceSeq = null,
        CancellationToken ct       = default)
    {
        var qs = BuildQueryString(nid, sinceSeq);
        var url = $"v1/log/entries{qs}";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw await BuildErrorAsync(resp, ct);

        var wrapper = await resp.Content.ReadFromJsonAsync<EntriesResponse>(s_json, ct);
        return (IReadOnlyList<ReputationLogEntry>?)wrapper?.Entries ?? Array.Empty<ReputationLogEntry>();
    }

    /// <summary>Get the current Signed Tree Head from the log operator.</summary>
    public async Task<SignedTreeHead> GetSthAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("v1/log/sth", ct);
        if (!resp.IsSuccessStatusCode)
            throw await BuildErrorAsync(resp, ct);

        return await resp.Content.ReadFromJsonAsync<SignedTreeHead>(s_json, ct)
            ?? throw new InvalidOperationException("Log operator returned an empty STH.");
    }

    /// <summary>Get the Merkle inclusion proof for the entry at <paramref name="seq"/>.</summary>
    public async Task<InclusionProof> GetProofAsync(ulong seq, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"v1/log/proof?seq={seq}", ct);
        if (!resp.IsSuccessStatusCode)
            throw await BuildErrorAsync(resp, ct);

        return await resp.Content.ReadFromJsonAsync<InclusionProof>(s_json, ct)
            ?? throw new InvalidOperationException("Log operator returned an empty proof.");
    }

    /// <summary>
    /// Fetch a gossip STH from a log mirror or relay (NPS-RFC-0004 §7, Phase 3).
    /// </summary>
    public async Task<SignedTreeHead> GetGossipSthAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("v1/log/gossip/sth", ct);
        if (!resp.IsSuccessStatusCode)
            throw await BuildErrorAsync(resp, ct);

        return await resp.Content.ReadFromJsonAsync<SignedTreeHead>(s_json, ct)
            ?? throw new InvalidOperationException("Log operator returned an empty gossip STH.");
    }

    // ── Merkle verification ───────────────────────────────────────────────────

    /// <summary>
    /// Verify that <paramref name="entry"/> is included in the Merkle tree
    /// described by <paramref name="sth"/> using <paramref name="proof"/>.
    ///
    /// <para>Returns <c>true</c> iff:</para>
    /// <list type="number">
    ///   <item>The computed leaf hash matches <see cref="InclusionProof.LeafHash"/>.</item>
    ///   <item>Folding the audit path over the leaf hash yields the root hash
    ///         in <see cref="SignedTreeHead.Sha256RootHash"/>.</item>
    /// </list>
    ///
    /// <para>Callers SHOULD also verify the STH's operator signature before trusting
    /// the root hash (Phase 3 gossip protocol).</para>
    /// </summary>
    public static bool VerifyInclusion(
        InclusionProof     proof,
        SignedTreeHead     sth,
        ReputationLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(sth);
        ArgumentNullException.ThrowIfNull(entry);

        // 1. Compute leaf hash: SHA256(0x00 || canonical_json_bytes)
        var leafJson  = LeafCanonicalJson(entry);
        var leafBytes = Encoding.UTF8.GetBytes(leafJson);
        var leafInput = new byte[1 + leafBytes.Length];
        leafInput[0]  = 0x00;
        leafBytes.CopyTo(leafInput, 1);
        var nodeHash = SHA256.HashData(leafInput);

        // 2. The supplied leaf_hash in the proof must match
        byte[] suppliedLeafHash;
        try { suppliedLeafHash = Crypto.NipSigner.FromBase64Url(proof.LeafHash); }
        catch { return false; }
        if (!CryptographicOperations.FixedTimeEquals(nodeHash, suppliedLeafHash))
            return false;

        // 3. Fold audit path (RFC 9162 §2.1.3.2)
        var idx = proof.LeafIndex;
        var buf = new byte[65]; // 1 prefix + 32 left + 32 right
        for (int i = 0; i < proof.AuditPath.Count; i++)
        {
            byte[] sibling;
            try { sibling = Crypto.NipSigner.FromBase64Url(proof.AuditPath[i]); }
            catch { return false; }
            if (sibling.Length != 32) return false;

            buf[0] = 0x01;
            if (((idx >> i) & 1UL) == 0UL)
            {
                // Current node is a left child → hash(0x01 || current || sibling)
                nodeHash.CopyTo(buf, 1);
                sibling.CopyTo(buf, 33);
            }
            else
            {
                // Current node is a right child → hash(0x01 || sibling || current)
                sibling.CopyTo(buf, 1);
                nodeHash.CopyTo(buf, 33);
            }
            nodeHash = SHA256.HashData(buf);
        }

        // 4. Compare folded root to STH root hash
        byte[] expectedRoot;
        try { expectedRoot = Crypto.NipSigner.FromBase64Url(sth.Sha256RootHash); }
        catch { return false; }
        return CryptographicOperations.FixedTimeEquals(nodeHash, expectedRoot);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static string BuildQueryString(string? nid, ulong? sinceSeq)
    {
        if (nid is null && sinceSeq is null) return string.Empty;
        var sb = new StringBuilder("?");
        if (nid      is not null) sb.Append($"nid={Uri.EscapeDataString(nid)}&");
        if (sinceSeq is not null) sb.Append($"since={sinceSeq}");
        return sb.ToString().TrimEnd('&');
    }

    private static async Task<ReputationLogException> BuildErrorAsync(
        HttpResponseMessage resp,
        CancellationToken   ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error",  out var err) &&
                root.TryGetProperty("status", out var st))
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : body;
                return new ReputationLogException(err.GetString() ?? "UNKNOWN", st.GetString() ?? "UNKNOWN", msg);
            }
        }
        catch { /* fall through */ }

        return new ReputationLogException(
            "UNKNOWN",
            $"HTTP-{(int)resp.StatusCode}",
            $"Reputation log returned {(int)resp.StatusCode}: {body}");
    }

    /// <summary>
    /// Canonical JSON of <paramref name="entry"/> INCLUDING the signature field,
    /// for use as Merkle leaf content (distinct from the signing canonical form
    /// which excludes <c>signature</c>).
    /// </summary>
    internal static string LeafCanonicalJson(ReputationLogEntry entry)
    {
        var json  = JsonSerializer.Serialize(entry, s_json);
        using var doc = JsonDocument.Parse(json);
        var sb    = new StringBuilder();
        WriteAllSorted(doc.RootElement, sb);
        return sb.ToString();
    }

    private static void WriteAllSorted(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var props = el.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                for (int i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(JsonEncodedText.Encode(props[i].Name)).Append("\":");
                    WriteAllSorted(props[i].Value, sb);
                }
                sb.Append('}');
                break;

            case JsonValueKind.Array:
                sb.Append('[');
                var items = el.EnumerateArray().ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteAllSorted(items[i], sb);
                }
                sb.Append(']');
                break;

            default:
                sb.Append(el.GetRawText());
                break;
        }
    }

    // ── Wire DTOs ─────────────────────────────────────────────────────────────

    private sealed class EntriesResponse
    {
        [JsonPropertyName("entries")]
        public List<ReputationLogEntry>? Entries { get; init; }
    }
}

/// <summary>
/// Raised when the reputation-log HTTP API returns an error or the connection
/// cannot be established.
/// </summary>
public sealed class ReputationLogException : Exception
{
    public ReputationLogException(string nipErrorCode, string npsStatus, string? message)
        : base(message)
    {
        NipErrorCode = nipErrorCode;
        NpsStatus    = npsStatus;
    }

    /// <summary>NIP error code — e.g. <c>NIP-REPUTATION-LOG-UNREACHABLE</c>.</summary>
    public string NipErrorCode { get; }

    /// <summary>NPS status code — e.g. <c>HTTP-503</c>.</summary>
    public string NpsStatus { get; }
}
