// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

// NOP 3-node DAG end-to-end demo (Phase 2 exit criterion).
//
// Spins up three NWP Action Nodes on loopback ports, wires them together
// with a linear task DAG, and lets the NOP orchestrator execute the pipeline
// over real HTTP. Output is printed to stdout so a human can eyeball it.
//
//   Run:  dotnet run --project impl/dotnet/samples/NPS.Samples.NopDag

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using NPS.NOP.Storage;
using NPS.NWP.ActionNode;
using NPS.NWP.Extensions;
using NPS.Samples.NopDag;
using NPS.Samples.NopDag.Orchestration;
using NPS.Samples.NopDag.Providers;

const string FetchNid     = "urn:nps:agent:demo:fetch";
const string SummarizeNid = "urn:nps:agent:demo:summarize";
const string PublishNid   = "urn:nps:agent:demo:publish";

var fetchUrl     = "http://127.0.0.1:17441";
var summarizeUrl = "http://127.0.0.1:17442";
var publishUrl   = "http://127.0.0.1:17443";

// ── Spin up 3 Action Node hosts ────────────────────────────────────────────

var hosts = new[]
{
    NodeHostBuilder.Build<FetchArticlesProvider>(fetchUrl,
        nodeId: "urn:nps:node:demo:fetch",
        actionId: "content.fetch_articles",
        description: "Return a curated list of articles on the requested topic."),

    NodeHostBuilder.Build<SummarizeProvider>(summarizeUrl,
        nodeId: "urn:nps:node:demo:summarize",
        actionId: "content.summarize",
        description: "Produce one-line summaries for a list of articles."),

    NodeHostBuilder.Build<PublishDigestProvider>(publishUrl,
        nodeId: "urn:nps:node:demo:publish",
        actionId: "content.publish_digest",
        description: "Render the final Markdown digest."),
};

foreach (var h in hosts) await h.StartAsync();

Console.WriteLine("═══ NOP 3-node DAG demo ═══");
Console.WriteLine($"  node 1 ({FetchNid})     → {fetchUrl}/invoke");
Console.WriteLine($"  node 2 ({SummarizeNid}) → {summarizeUrl}/invoke");
Console.WriteLine($"  node 3 ({PublishNid})   → {publishUrl}/invoke");
Console.WriteLine();

// ── Build orchestrator wired to the 3 hosts ────────────────────────────────

var routes = new Dictionary<string, string>
{
    [FetchNid]     = fetchUrl,
    [SummarizeNid] = summarizeUrl,
    [PublishNid]   = publishUrl,
};

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var worker     = new HttpNopWorkerClient(routes, http);
var store      = new InMemoryNopTaskStore();
var orch       = new NopOrchestrator(worker, store,
    new NopOrchestratorOptions { ValidateSenderNid = false });

// ── Build the 3-node DAG ───────────────────────────────────────────────────
//
//   fetch ──► summarize ──► publish
//
// input_mapping threads data along the chain via JSONPath-style references.

// The fetch node has no upstream dependencies, so it takes no input_mapping —
// it uses its provider's default topic. A real caller could thread a topic
// through TaskFrame.Context or pre-seed an upstream "input" node.

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
                Agent  = FetchNid,
            },
            new DagNode
            {
                Id        = "summarize",
                Action    = "nwp://demo/content.summarize",
                Agent     = SummarizeNid,
                InputFrom = ["fetch"],
                // Pull the articles array out of node "fetch"'s result.
                InputMapping = ParseMap("""{ "articles": "$.fetch.articles" }"""),
            },
            new DagNode
            {
                Id        = "publish",
                Action    = "nwp://demo/content.publish_digest",
                Agent     = PublishNid,
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

Console.WriteLine($"► Submitting task {task.TaskId}");
Console.WriteLine();

var result = await orch.ExecuteAsync(task);

Console.WriteLine($"◄ Final state: {result.FinalState}");
Console.WriteLine($"  Completed {result.NodeResults.Count} / 3 nodes");
Console.WriteLine();

if (result.FinalState != TaskState.Completed)
{
    Console.WriteLine($"Task did not complete: {result.ErrorCode} — {result.ErrorMessage}");
    Environment.ExitCode = 1;
}
else
{
    foreach (var nid in new[] { "fetch", "summarize", "publish" })
    {
        Console.WriteLine($"─── {nid} ───");
        Console.WriteLine(JsonSerializer.Serialize(result.NodeResults[nid],
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine();
    }

    // Pretty-print the final digest on its own — this is what a downstream
    // consumer would actually use.
    if (result.NodeResults["publish"].TryGetProperty("digest_md", out var digest))
    {
        Console.WriteLine("══════ Published digest ══════");
        Console.WriteLine(digest.GetString());
    }
}

foreach (var h in hosts) await h.StopAsync();

return Environment.ExitCode;

// ── Local helpers ──────────────────────────────────────────────────────────

static Dictionary<string, JsonElement> ParseMap(string json)
{
    using var doc = JsonDocument.Parse(json);
    var map = new Dictionary<string, JsonElement>();
    foreach (var p in doc.RootElement.EnumerateObject())
        map[p.Name] = p.Value.Clone();
    return map;
}
