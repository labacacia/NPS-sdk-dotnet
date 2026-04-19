// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;

namespace NPS.Samples.NopDag.Providers;

/// <summary>
/// Node 2 of the demo pipeline. Takes an <c>articles</c> array (delivered by the
/// orchestrator via input_mapping from node 1) and emits one-line summaries.
/// In a real deployment this would be a call to a summarisation model.
/// </summary>
public sealed class SummarizeProvider : IActionNodeProvider
{
    public Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame, ActionContext context, CancellationToken ct = default)
    {
        if (frame.Params is null || !frame.Params.Value.TryGetProperty("articles", out var articles) ||
            articles.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("summarize: expected params.articles to be an array");
        }

        var summaries = new List<object>();
        foreach (var a in articles.EnumerateArray())
        {
            var id    = a.GetProperty("id").GetString()!;
            var title = a.GetProperty("title").GetString()!;
            var body  = a.GetProperty("body").GetString()!;

            // Trivial deterministic summarisation: title + first sentence of body.
            var firstSentence = body.Split('.', 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            summaries.Add(new { id, summary = $"{title} — {firstSentence}." });
        }

        var json = JsonSerializer.Serialize(new { summaries });
        return Task.FromResult(new ActionExecutionResult
        {
            Result    = JsonDocument.Parse(json).RootElement,
            AnchorRef = "nps://sample/anchors/summary-list/v1",
        });
    }
}
