// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;

namespace NPS.Samples.NopDag.Providers;

/// <summary>
/// Node 3 of the demo pipeline. Takes a <c>summaries</c> array and a <c>topic</c>
/// (both threaded by the orchestrator) and emits a Markdown digest string.
/// </summary>
public sealed class PublishDigestProvider : IActionNodeProvider
{
    public Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame, ActionContext context, CancellationToken ct = default)
    {
        if (frame.Params is null)
            throw new InvalidOperationException("publish: params required");

        var topic = frame.Params.Value.TryGetProperty("topic", out var t)
            ? t.GetString() ?? "(untitled)" : "(untitled)";

        if (!frame.Params.Value.TryGetProperty("summaries", out var summaries) ||
            summaries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("publish: expected params.summaries to be an array");
        }

        var sb = new StringBuilder();
        sb.Append("# Daily digest — ").AppendLine(topic);
        sb.AppendLine();
        foreach (var s in summaries.EnumerateArray())
        {
            sb.Append("- ").AppendLine(s.GetProperty("summary").GetString());
        }

        var json = JsonSerializer.Serialize(new
        {
            topic,
            article_count = summaries.GetArrayLength(),
            digest_md     = sb.ToString(),
        });

        return Task.FromResult(new ActionExecutionResult
        {
            Result    = JsonDocument.Parse(json).RootElement,
            AnchorRef = "nps://sample/anchors/digest/v1",
        });
    }
}
