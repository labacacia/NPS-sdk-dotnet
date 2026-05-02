// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;

namespace NPS.Samples.NopDag.Providers;

/// <summary>
/// Node 1 of the demo pipeline. Takes an optional <c>topic</c> parameter and
/// returns a curated list of articles. Deterministic output — in a real
/// deployment this would be a search-index or CMS adapter.
/// </summary>
public sealed class FetchArticlesProvider : IActionNodeProvider
{
    public Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame, ActionContext context, CancellationToken ct = default)
    {
        var topic = "NPS protocol suite";
        if (frame.Params?.TryGetProperty("topic", out var t) == true && t.ValueKind == JsonValueKind.String)
            topic = t.GetString()!;

        var json = JsonSerializer.Serialize(new
        {
            topic,
            articles = new object[]
            {
                new { id = "a-01", title = "Why Agents need a Schema-first Protocol",
                      body  = "NWP treats schema as a first-class resource anchored once per session." },
                new { id = "a-02", title = "Cognon Budget: a tokenizer-agnostic cost model",
                      body  = "Counting in CGN lets us compare traffic across models without committing to one tokenizer." },
                new { id = "a-03", title = "Three Node Types, one Port",
                      body  = "Memory/Action/Complex nodes multiplex on 17433 — the frame type code carries the routing." },
            },
        });

        return Task.FromResult(new ActionExecutionResult
        {
            Result    = JsonDocument.Parse(json).RootElement,
            AnchorRef = "nps://sample/anchors/article-list/v1",
        });
    }
}
