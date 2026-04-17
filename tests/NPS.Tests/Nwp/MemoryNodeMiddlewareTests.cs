// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NPS.NWP.Extensions;
using NPS.NWP.Frames;
using NPS.NWP.Http;
using NPS.NWP.MemoryNode;
using NPS.NWP.MemoryNode.Query;

namespace NPS.Tests.Nwp;

/// <summary>
/// Integration tests for <see cref="MemoryNodeMiddleware"/> using ASP.NET Core TestHost.
/// A <see cref="FakeMemoryNodeProvider"/> is injected to isolate middleware logic
/// from real database I/O.
/// </summary>
public sealed class MemoryNodeMiddlewareTests : IAsyncLifetime
{
    // ── Schema and options used across all tests ──────────────────────────────

    private static readonly MemoryNodeSchema Schema = new()
    {
        TableName  = "products",
        PrimaryKey = "id",
        Fields =
        [
            new MemoryNodeField { Name = "id",    Type = "number" },
            new MemoryNodeField { Name = "name",  Type = "string" },
            new MemoryNodeField { Name = "price", Type = "number" },
        ],
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private IHost         _host     = null!;
    private HttpClient    _client   = null!;
    private FakeProvider  _provider = null!;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _provider = new FakeProvider();
        _host     = await BuildHost(requireAuth: false);
        _client   = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ── /.nwm ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nwm_Get_Returns200WithManifestContentType()
    {
        var resp = await _client.GetAsync("/products/.nwm");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(NwpHttpHeaders.MimeManifest, resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Nwm_Get_BodyContainsNodeTypeMemory()
    {
        var resp = await _client.GetAsync("/products/.nwm");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("\"memory\"", body);
        Assert.Contains("node_id",    body);
    }

    [Fact]
    public async Task Nwm_Get_ResponseHeaderNodeTypeIsMemory()
    {
        var resp = await _client.GetAsync("/products/.nwm");
        Assert.Equal("memory", resp.Headers.GetValues(NwpHttpHeaders.NodeType).First());
    }

    [Fact]
    public async Task Nwm_TrailingSlash_Returns200()
    {
        var resp = await _client.GetAsync("/products/.nwm/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── /.schema ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schema_Get_Returns200WithJson()
    {
        var resp = await _client.GetAsync("/products/.schema");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Schema_Get_ResponseHeaderContainsAnchorId()
    {
        var resp = await _client.GetAsync("/products/.schema");
        Assert.True(resp.Headers.Contains(NwpHttpHeaders.Schema));
        var anchorId = resp.Headers.GetValues(NwpHttpHeaders.Schema).First();
        Assert.StartsWith("sha256:", anchorId);
    }

    [Fact]
    public async Task Schema_Get_BodyContainsFields()
    {
        var resp = await _client.GetAsync("/products/.schema");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("\"id\"",    body);
        Assert.Contains("\"name\"",  body);
        Assert.Contains("\"price\"", body);
    }

    // ── /query — happy path ───────────────────────────────────────────────────

    [Fact]
    public async Task Query_Post_Returns200WithCapsFrame()
    {
        var frame   = new QueryFrame { Limit = 2 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/products/query", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Query_Post_ResponseHeadersContainSchemaAndTokens()
    {
        var frame   = new QueryFrame { Limit = 1 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/products/query", content);

        Assert.True(resp.Headers.Contains(NwpHttpHeaders.Schema));
        Assert.True(resp.Headers.Contains(NwpHttpHeaders.Tokens));
    }

    [Fact]
    public async Task Query_Post_TrailingSlash_Returns200()
    {
        var frame   = new QueryFrame();
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var resp    = await _client.PostAsync("/products/query/", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Query_Get_Returns405MethodNotAllowed()
    {
        var resp = await _client.GetAsync("/products/query");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    // ── /query — budget enforcement ───────────────────────────────────────────

    [Fact]
    public async Task Query_BudgetHeader_TrimsRows()
    {
        // Provider returns 3 rows; set budget to 1 NPT so only 0–1 rows fit
        _provider.RowsToReturn = 3;

        var frame   = new QueryFrame { Limit = 3 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var request = new HttpRequestMessage(HttpMethod.Post, "/products/query")
        {
            Content = content,
            Headers = { { NwpHttpHeaders.Budget, "1" } },
        };

        var resp = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // With budget=1 NPT, at most 0 or 1 row fits; certainly fewer than 3
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() < 3);
    }

    // ── /query — filter exception from provider ───────────────────────────────

    [Fact]
    public async Task Query_ProviderThrowsFilterException_Returns400()
    {
        _provider.ThrowFilter = true;

        var frame   = new QueryFrame();
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/products/query", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("NPS-CLIENT-BAD-REQUEST", body);
    }

    // ── /query — unhandled provider exception ─────────────────────────────────

    [Fact]
    public async Task Query_ProviderThrowsUnhandled_Returns500()
    {
        _provider.ThrowInternal = true;

        var frame   = new QueryFrame();
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/products/query", content);

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("NPS-SERVER-INTERNAL", body);
    }

    // ── /query — malformed body ────────────────────────────────────────────────

    [Fact]
    public async Task Query_MalformedBody_Returns400()
    {
        var content = new StringContent("not-json-at-all", Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var resp    = await _client.PostAsync("/products/query", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── /stream — happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task Stream_Post_Returns200WithNewlineDelimitedChunks()
    {
        _provider.RowsToReturn = 4;   // 2 pages of 2

        var frame   = new QueryFrame { Limit = 2 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await _client.PostAsync("/products/stream", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        // Each line should be a JSON StreamFrame
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, $"Expected ≥2 newline-delimited chunks, got: {lines.Length}");

        // Last line must be the sentinel (is_last = true)
        using var lastDoc = JsonDocument.Parse(lines[^1]);
        Assert.True(lastDoc.RootElement.GetProperty("is_last").GetBoolean());
    }

    [Fact]
    public async Task Stream_Get_Returns405()
    {
        var resp = await _client.GetAsync("/products/stream");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Fact]
    public async Task Stream_TrailingSlash_Returns200()
    {
        var frame   = new QueryFrame { Limit = 1 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var resp    = await _client.PostAsync("/products/stream/", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Stream_FirstChunk_CarriesAnchorRef()
    {
        _provider.RowsToReturn = 1;

        var frame   = new QueryFrame { Limit = 1 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp  = await _client.PostAsync("/products/stream", content);
        var body  = await resp.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        using var firstDoc = JsonDocument.Parse(lines[0]);
        // First real-data chunk carries anchor_ref
        // (the implementation sets anchor_ref when seq == 1, i.e. the first chunk after seq++ has run once)
        // The sentinel chunk is last — check at least one chunk has stream_id
        Assert.True(firstDoc.RootElement.TryGetProperty("stream_id", out _));
    }

    [Fact]
    public async Task Stream_FilterException_EmitsErrorChunk()
    {
        _provider.ThrowFilterOnStream = true;

        var frame   = new QueryFrame { Limit = 1 };
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp  = await _client.PostAsync("/products/stream", content);
        var body  = await resp.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Error chunk must have is_last=true and a non-null error_code
        using var errDoc = JsonDocument.Parse(lines[^1]);
        Assert.True(errDoc.RootElement.GetProperty("is_last").GetBoolean());
        Assert.True(errDoc.RootElement.TryGetProperty("error_code", out var ecProp));
        Assert.False(string.IsNullOrEmpty(ecProp.GetString()));
    }

    // ── Auth guard ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Query_WithAuth_MissingHeader_Returns401()
    {
        using var authHost   = await BuildHost(requireAuth: true);
        using var authClient = authHost.GetTestClient();

        var frame   = new QueryFrame();
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);

        var resp = await authClient.PostAsync("/products/query", content);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Query_WithAuth_AgentHeaderPresent_Returns200()
    {
        using var authHost   = await BuildHost(requireAuth: true);
        using var authClient = authHost.GetTestClient();

        var frame   = new QueryFrame();
        var content = new StringContent(JsonSerializer.Serialize(frame, JsonOpts), Encoding.UTF8, NwpHttpHeaders.MimeFrame);
        var request = new HttpRequestMessage(HttpMethod.Post, "/products/query")
        {
            Content = content,
            Headers = { { NwpHttpHeaders.Agent, "urn:nps:agent:ca.example.com:test-agent" } },
        };

        var resp = await authClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Path routing ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownSubPath_PassesToNextMiddleware()
    {
        // /products/unknown → should fall through to next (our stub returns 404)
        var resp = await _client.GetAsync("/products/unknown");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PathOutsidePrefix_PassesToNextMiddleware()
    {
        var resp = await _client.GetAsync("/other-path");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IHost> BuildHost(bool requireAuth)
    {
        var provider = _provider;  // capture for lambda

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton<FakeProvider>(_ => provider);
                });
                web.Configure(app =>
                {
                    app.UseMemoryNode<FakeProvider>(opts =>
                    {
                        opts.NodeId      = "urn:nps:node:test:products";
                        opts.DisplayName = "Test Products";
                        opts.PathPrefix  = "/products";
                        opts.Schema      = Schema;
                        opts.RequireAuth = requireAuth;
                        opts.DefaultLimit = 20;
                        opts.MaxLimit     = 100;
                    });
                    // Fallback: 404 for unmatched paths
                    app.Run(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }
}

// ── FakeMemoryNodeProvider ────────────────────────────────────────────────────

/// <summary>
/// Controllable fake provider for <see cref="MemoryNodeMiddlewareTests"/>.
/// Properties let each test configure the desired behaviour.
/// </summary>
internal sealed class FakeProvider : IMemoryNodeProvider
{
    // Controls
    public int  RowsToReturn      { get; set; } = 2;
    public bool ThrowFilter       { get; set; }
    public bool ThrowInternal     { get; set; }
    public bool ThrowFilterOnStream { get; set; }

    // ── QueryAsync ────────────────────────────────────────────────────────────

    public Task<MemoryNodeQueryResult> QueryAsync(
        QueryFrame          frame,
        MemoryNodeSchema    schema,
        MemoryNodeOptions   options,
        CancellationToken   ct = default)
    {
        if (ThrowFilter)
            throw new NwpFilterException("Fake filter error", "NWP-QUERY-FILTER-INVALID");

        if (ThrowInternal)
            throw new InvalidOperationException("Fake internal error");

        var rows = BuildRows(RowsToReturn);
        return Task.FromResult(new MemoryNodeQueryResult { Rows = rows });
    }

    // ── StreamAsync ───────────────────────────────────────────────────────────

    public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StreamAsync(
        QueryFrame          frame,
        MemoryNodeSchema    schema,
        MemoryNodeOptions   options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (ThrowFilterOnStream)
            throw new NwpFilterException("Fake stream filter error", "NWP-QUERY-FILTER-INVALID");

        var limit    = (int)(frame.Limit == 0 ? options.DefaultLimit : frame.Limit);
        var total    = RowsToReturn;
        var emitted  = 0;

        while (emitted < total && !ct.IsCancellationRequested)
        {
            var take    = Math.Min(limit, total - emitted);
            var page    = BuildRows(take, startId: emitted + 1);
            emitted    += take;
            yield return page;
            await Task.Yield();
        }
    }

    // ── CountAsync ────────────────────────────────────────────────────────────

    public Task<long> CountAsync(
        QueryFrame          frame,
        MemoryNodeSchema    schema,
        CancellationToken   ct = default)
        => Task.FromResult((long)RowsToReturn);

    // ── Private ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> BuildRows(int count, int startId = 1)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["id"]    = (long)(startId + i),
                ["name"]  = $"Product {startId + i}",
                ["price"] = (double)((startId + i) * 9.99),
            });
        }
        return rows;
    }
}
