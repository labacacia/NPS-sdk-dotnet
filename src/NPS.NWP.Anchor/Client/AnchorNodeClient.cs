// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Anchor.Topology;
using NPS.NWP.Http;

namespace NPS.NWP.Anchor.Client;

/// <summary>
/// Typed client for an Anchor Node's reserved query types
/// (NPS-2 §12 — <c>topology.snapshot</c> and <c>topology.stream</c>).
///
/// <para>The client is transport-agnostic in shape but ships with a single
/// HTTP-mode implementation that talks to <c>NPS.NWP.Anchor.AnchorNodeMiddleware</c>'s
/// <c>/query</c> and <c>/subscribe</c> endpoints. Pass either a configured
/// <see cref="HttpClient"/> with the Anchor's base URL, or rely on the
/// constructor that builds one.</para>
///
/// <para>The client never assumes auth. Callers MUST configure an
/// <c>X-NWP-Agent</c> header (or whatever the deployment requires) on the
/// <see cref="HttpClient"/> before calling.</para>
/// </summary>
public sealed class AnchorNodeClient
{
    private readonly HttpClient _http;
    private readonly string     _pathPrefix;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = false,
    };

    /// <summary>
    /// Build a client over an existing <see cref="HttpClient"/>. Use this when
    /// the host application owns the HttpClient lifetime
    /// (e.g. <c>HttpClientFactory</c>, TestServer, etc.).
    /// </summary>
    /// <param name="http">HTTP client; <see cref="HttpClient.BaseAddress"/> SHOULD point at the Anchor host.</param>
    /// <param name="pathPrefix">Anchor middleware's <c>PathPrefix</c>; e.g. <c>"/anchor"</c>. Empty for root mounting.</param>
    public AnchorNodeClient(HttpClient http, string pathPrefix = "")
    {
        _http       = http ?? throw new ArgumentNullException(nameof(http));
        _pathPrefix = pathPrefix.TrimEnd('/');
    }

    // ── topology.snapshot ────────────────────────────────────────────────────

    /// <summary>
    /// Fetch the current cluster topology (NPS-2 §12.1). Returns a fully
    /// materialized <see cref="TopologySnapshot"/>; caller MAY combine it with
    /// a subsequent <see cref="SubscribeAsync"/> for live updates.
    /// </summary>
    public async Task<TopologySnapshot> GetSnapshotAsync(
        TopologyScope     scope     = TopologyScope.Cluster,
        TopologyInclude   include   = TopologyInclude.Default,
        byte              depth     = 1,
        string?           targetNid = null,
        CancellationToken ct        = default)
    {
        var body = BuildSnapshotPayload(scope, include, depth, targetNid);

        using var resp = await _http.PostAsJsonAsync($"{_pathPrefix}/query", body, Json, ct);
        if (!resp.IsSuccessStatusCode)
            throw await BuildErrorAsync("topology.snapshot", resp, ct);

        var caps = await resp.Content.ReadFromJsonAsync<CapsFrame>(Json, ct)
            ?? throw new InvalidOperationException("Anchor returned an empty CapsFrame.");

        if (caps.Data.Count != 1)
            throw new InvalidOperationException(
                $"topology.snapshot expected exactly 1 data row; got {caps.Data.Count}.");

        return JsonSerializer.Deserialize<TopologySnapshot>(caps.Data[0].GetRawText(), Json)
            ?? throw new InvalidOperationException("Failed to decode topology snapshot.");
    }

    // ── topology.stream ──────────────────────────────────────────────────────

    /// <summary>
    /// Subscribe to live topology changes (NPS-2 §12.2). The first chunk is
    /// always a subscription ack and is not yielded; subsequent chunks become
    /// <see cref="TopologyEvent"/> instances. Disposal of the enumerator —
    /// including breaking out of the loop or cancelling the
    /// <see cref="CancellationToken"/> — closes the underlying HTTP stream.
    ///
    /// <para>Callers receiving <see cref="ResyncRequired"/> MUST issue a fresh
    /// <see cref="GetSnapshotAsync"/> before resubscribing.</para>
    /// </summary>
    public async IAsyncEnumerable<TopologyEvent> SubscribeAsync(
        TopologyFilter?                            filter       = null,
        ulong?                                     sinceVersion = null,
        TopologyScope                              scope        = TopologyScope.Cluster,
        [EnumeratorCancellation] CancellationToken ct           = default)
    {
        var body = BuildStreamPayload(scope, filter, sinceVersion);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_pathPrefix}/subscribe")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        // Hint the server that we want the response chunked-streamed rather
        // than buffered in full.
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw await BuildErrorAsync("topology.stream", resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // First line is the subscription ack — we discard it and yield events.
        var ackLine = await reader.ReadLineAsync(ct);
        if (ackLine is null)
            yield break;

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            if (line is null) yield break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Mid-stream protocol error envelope (ErrorFrame shape) ends the stream.
            if (TryParseError(line, out var protoErr))
                throw protoErr;

            var ev = ParseEvent(line);
            if (ev is null) continue;

            yield return ev;

            // The subscriber is required to re-snapshot after a resync_required;
            // there's no point pushing further events on the same stream.
            if (ev is ResyncRequired) yield break;
        }
    }

    // ── Wire helpers ─────────────────────────────────────────────────────────

    private static object BuildSnapshotPayload(
        TopologyScope scope, TopologyInclude include, byte depth, string? targetNid)
    {
        var topology = new Dictionary<string, object?>
        {
            ["scope"] = scope == TopologyScope.Cluster ? TopologyWire.ScopeCluster : TopologyWire.ScopeMember,
        };

        var includes = IncludesToWire(include);
        if (includes.Count > 0) topology["include"] = includes;
        if (depth != 1)         topology["depth"]    = depth;
        if (targetNid is not null) topology["target_nid"] = targetNid;

        return new
        {
            type = TopologyWire.TypeSnapshot,
            topology,
        };
    }

    private static object BuildStreamPayload(
        TopologyScope scope, TopologyFilter? filter, ulong? sinceVersion)
    {
        var topology = new Dictionary<string, object?>
        {
            ["scope"] = scope == TopologyScope.Cluster ? TopologyWire.ScopeCluster : TopologyWire.ScopeMember,
        };
        if (filter is not null)        topology["filter"]        = filter;
        if (sinceVersion is { } sv)    topology["since_version"] = sv;

        return new
        {
            type      = TopologyWire.TypeStream,
            action    = "subscribe",
            stream_id = Guid.NewGuid().ToString("N"),
            topology,
        };
    }

    private static List<string> IncludesToWire(TopologyInclude include)
    {
        var list = new List<string>(4);
        if (include.HasFlag(TopologyInclude.Members))      list.Add(TopologyWire.IncludeMembers);
        if (include.HasFlag(TopologyInclude.Capabilities)) list.Add(TopologyWire.IncludeCapabilities);
        if (include.HasFlag(TopologyInclude.Tags))         list.Add(TopologyWire.IncludeTags);
        if (include.HasFlag(TopologyInclude.Metrics))      list.Add(TopologyWire.IncludeMetrics);
        // Default include is "members" only — represent that as the empty list
        // server-side via list.Count == 1 && list[0] == "members" (no need to
        // suppress; it's still the same shape).
        return list;
    }

    private static TopologyEvent? ParseEvent(string line)
    {
        var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("event_type", out var t) || t.ValueKind != JsonValueKind.String)
            return null;

        ulong seq = 0;
        if (root.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number)
            seq = s.GetUInt64();

        var payload = root.TryGetProperty("payload", out var p) ? p : default;
        var et = t.GetString();

        return et switch
        {
            TopologyWire.EventMemberJoined => new MemberJoined
            {
                Version = seq,
                Member  = JsonSerializer.Deserialize<MemberInfo>(payload.GetRawText(), Json)!,
            },
            TopologyWire.EventMemberLeft => new MemberLeft
            {
                Version = seq,
                Nid     = payload.GetProperty("nid").GetString() ?? string.Empty,
            },
            TopologyWire.EventMemberUpdated => new MemberUpdated
            {
                Version = seq,
                Nid     = payload.GetProperty("nid").GetString() ?? string.Empty,
                Changes = JsonSerializer.Deserialize<MemberChanges>(
                              payload.GetProperty("changes").GetRawText(), Json)!,
            },
            TopologyWire.EventAnchorState => new AnchorState
            {
                Version = seq,
                Field   = payload.GetProperty("field").GetString() ?? string.Empty,
                Details = payload.TryGetProperty("details", out var det) ? det : null,
            },
            TopologyWire.EventResyncRequired => new ResyncRequired
            {
                Version = 0,
                Reason  = payload.GetProperty("reason").GetString() ?? "unknown",
            },
            _ => null,
        };
    }

    private static bool TryParseError(string line, out AnchorTopologyException error)
    {
        error = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            // ErrorFrame shape: { status, error, message }. Distinguish from an
            // event line by the absence of "event_type".
            if (root.TryGetProperty("event_type", out _)) return false;
            if (!root.TryGetProperty("error", out var e) || e.ValueKind != JsonValueKind.String) return false;
            if (!root.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.String) return false;

            var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;

            error = new AnchorTopologyException(e.GetString()!, s.GetString()!, msg);
            return true;
        }
        catch { return false; }
    }

    private static async Task<AnchorTopologyException> BuildErrorAsync(
        string operation, HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var err) &&
                root.TryGetProperty("status", out var st))
            {
                return new AnchorTopologyException(
                    err.GetString() ?? "UNKNOWN",
                    st.GetString() ?? "UNKNOWN",
                    root.TryGetProperty("message", out var m) ? m.GetString() : body);
            }
        }
        catch { /* fall through */ }

        return new AnchorTopologyException(
            "UNKNOWN", $"HTTP-{(int)resp.StatusCode}",
            $"{operation} returned {(int)resp.StatusCode}: {body}");
    }
}

/// <summary>
/// Surfaces an Anchor-side topology error to the caller. <see cref="NwpErrorCode"/>
/// matches one of the <c>NWP-TOPOLOGY-*</c> codes (or another NWP error
/// returned mid-stream); <see cref="NpsStatus"/> is the corresponding NPS
/// status code per <c>spec/status-codes.md</c>.
/// </summary>
public sealed class AnchorTopologyException : Exception
{
    public AnchorTopologyException(string nwpErrorCode, string npsStatus, string? message)
        : base(message)
    {
        NwpErrorCode = nwpErrorCode;
        NpsStatus    = npsStatus;
    }

    public string NwpErrorCode { get; }
    public string NpsStatus    { get; }
}
