// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using NPS.NOP.Storage;
using NPS.NWP.ActionNode;
using NPS.NWP.Extensions;
using NPS.Samples.NopDag.Orchestration;
using NPS.Samples.NopDag.Providers;

namespace NPS.Tests.Samples;

/// <summary>
/// Integration guard for the NOP 3-node DAG demo (Phase 2 exit criterion).
/// Runs the same pipeline as <c>impl/dotnet/samples/NPS.Samples.NopDag</c> but
/// with TestServers instead of real sockets, so CI can execute it deterministically.
/// </summary>
public class NopDagDemoTests
{
    [Fact]
    public async Task ThreeNodeLinearDag_CompletesEndToEnd()
    {
        const string fetchNid     = "urn:nps:agent:demo:fetch";
        const string summarizeNid = "urn:nps:agent:demo:summarize";
        const string publishNid   = "urn:nps:agent:demo:publish";

        using var fetchHost     = BuildNodeHost<FetchArticlesProvider>(
            nodeId: "urn:nps:node:demo:fetch",     actionId: "content.fetch_articles");
        using var summarizeHost = BuildNodeHost<SummarizeProvider>(
            nodeId: "urn:nps:node:demo:summarize", actionId: "content.summarize");
        using var publishHost   = BuildNodeHost<PublishDigestProvider>(
            nodeId: "urn:nps:node:demo:publish",   actionId: "content.publish_digest");

        var fetchServer     = fetchHost.GetTestServer();
        var summarizeServer = summarizeHost.GetTestServer();
        var publishServer   = publishHost.GetTestServer();

        // All TestServers default to http://localhost/. Give each a unique authority
        // so the outbound router can tell them apart.
        fetchServer.BaseAddress     = new Uri("http://fetch.test/");
        summarizeServer.BaseAddress = new Uri("http://summarize.test/");
        publishServer.BaseAddress   = new Uri("http://publish.test/");

        // Each TestServer has a fake base address — map NIDs to them.
        var routes = new Dictionary<string, string>
        {
            [fetchNid]     = fetchServer.BaseAddress.ToString(),
            [summarizeNid] = summarizeServer.BaseAddress.ToString(),
            [publishNid]   = publishServer.BaseAddress.ToString(),
        };

        // Routing handler dispatches to the correct TestServer based on host+port.
        var handler = new MultiTestServerHandler(new()
        {
            [fetchServer.BaseAddress]     = fetchServer.CreateHandler(),
            [summarizeServer.BaseAddress] = summarizeServer.CreateHandler(),
            [publishServer.BaseAddress]   = publishServer.CreateHandler(),
        });

        using var http   = new HttpClient(handler) { BaseAddress = null };
        var worker       = new HttpNopWorkerClient(routes, http);
        var store        = new InMemoryNopTaskStore();
        var orch         = new NopOrchestrator(worker, store,
            new NopOrchestratorOptions { ValidateSenderNid = false });

        var task = new TaskFrame
        {
            TaskId = Guid.NewGuid().ToString("D"),
            Dag    = new TaskDag
            {
                Nodes =
                [
                    new DagNode
                    {
                        Id     = "fetch",
                        Action = "nwp://demo/content.fetch_articles",
                        Agent  = fetchNid,
                    },
                    new DagNode
                    {
                        Id        = "summarize",
                        Action    = "nwp://demo/content.summarize",
                        Agent     = summarizeNid,
                        InputFrom = ["fetch"],
                        InputMapping = ParseMap("""{ "articles": "$.fetch.articles" }"""),
                    },
                    new DagNode
                    {
                        Id        = "publish",
                        Action    = "nwp://demo/content.publish_digest",
                        Agent     = publishNid,
                        InputFrom = ["fetch", "summarize"],
                        InputMapping = ParseMap("""
                        {
                            "topic":     "$.fetch.topic",
                            "summaries": "$.summarize.summaries"
                        }
                        """),
                    },
                ],
                Edges =
                [
                    new DagEdge { From = "fetch",     To = "summarize" },
                    new DagEdge { From = "summarize", To = "publish"   },
                ],
            },
            TimeoutMs = 15_000,
        };

        var result = await orch.ExecuteAsync(task);

        Assert.True(result.FinalState == TaskState.Completed,
            $"Expected Completed, got {result.FinalState}: {result.ErrorCode} — {result.ErrorMessage}");
        Assert.Equal(3, result.NodeResults.Count);

        // Fetch output was threaded all the way through to the digest.
        var publish = result.NodeResults["publish"];
        Assert.Equal(3, publish.GetProperty("article_count").GetInt32());
        Assert.Equal("NPS protocol suite", publish.GetProperty("topic").GetString());
        var digest = publish.GetProperty("digest_md").GetString()!;
        Assert.Contains("# Daily digest", digest);
        Assert.Contains("Three Node Types, one Port", digest);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IHost BuildNodeHost<TProvider>(string nodeId, string actionId)
        where TProvider : class, IActionNodeProvider
    {
        var actions = new Dictionary<string, ActionSpec>
        {
            [actionId] = new ActionSpec { Async = false, Idempotent = true, TimeoutMsDefault = 5_000, TimeoutMsMax = 15_000 },
        };

        return new HostBuilder()
            .ConfigureWebHost(wb => wb
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    s.AddActionNode<TProvider>(o =>
                    {
                        o.NodeId     = nodeId;
                        o.Actions    = actions;
                        o.PathPrefix = "/";
                    });
                })
                .Configure(app => app.UseActionNode<TProvider>(o =>
                {
                    o.NodeId     = nodeId;
                    o.Actions    = actions;
                    o.PathPrefix = "/";
                })))
            .Start();
    }

    private static Dictionary<string, JsonElement> ParseMap(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, JsonElement>();
        foreach (var p in doc.RootElement.EnumerateObject())
            map[p.Name] = p.Value.Clone();
        return map;
    }

    /// <summary>
    /// Dispatches an outbound HttpRequest to the TestServer handler that owns its base URI.
    /// Allows a single HttpClient to fan out to multiple in-memory servers.
    /// </summary>
    private sealed class MultiTestServerHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpMessageInvoker> _byAuthority;

        public MultiTestServerHandler(Dictionary<Uri, HttpMessageHandler> handlers)
        {
            _byAuthority = handlers.ToDictionary(
                kv => kv.Key.GetLeftPart(UriPartial.Authority),
                kv => new HttpMessageInvoker(kv.Value, disposeHandler: false));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authority = request.RequestUri!.GetLeftPart(UriPartial.Authority);
            if (!_byAuthority.TryGetValue(authority, out var inv))
                throw new InvalidOperationException($"No TestServer registered for {authority}");
            return inv.SendAsync(request, cancellationToken);
        }
    }
}
