// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NPS.NOP.Frames;
using NPS.NOP.Orchestration;

namespace NPS.Samples.NopDag.Orchestration;

/// <summary>
/// Minimal NOP worker client for the demo: translates each <see cref="DelegateFrame"/>
/// into a synchronous NWP <c>POST /invoke</c> against the agent's registered base URL,
/// and wraps the CapsFrame response in a single terminal <see cref="AlignStreamFrame"/>.
/// <para>
/// This is intentionally narrow — production NOP workers would speak the full
/// AlignStream protocol, handle async tasks, and support cancellation. For the
/// exit-criterion demo we only need the happy path for linear 3-node DAGs.
/// </para>
/// </summary>
public sealed class HttpNopWorkerClient : INopWorkerClient
{
    /// <summary>Maps Worker-Agent NID → Action Node base URL (e.g. "http://127.0.0.1:17441").</summary>
    private readonly IReadOnlyDictionary<string, string> _routes;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public HttpNopWorkerClient(IReadOnlyDictionary<string, string> routes, HttpClient http)
    {
        _routes = routes;
        _http   = http;
    }

    public IAsyncEnumerable<AlignStreamFrame> DelegateAsync(DelegateFrame frame, CancellationToken ct = default)
        => DispatchAsync(frame, ct);

    public Task<PreflightResult> PreflightAsync(
        string agentNid, string action, long estimatedNpt = 0,
        IReadOnlyList<string>? requiredCapabilities = null, CancellationToken ct = default)
        => Task.FromResult(new PreflightResult
        {
            AgentNid  = agentNid,
            Available = _routes.ContainsKey(agentNid),
            UnavailableReason = _routes.ContainsKey(agentNid) ? null : $"no route for {agentNid}",
        });

    private async IAsyncEnumerable<AlignStreamFrame> DispatchAsync(
        DelegateFrame frame, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_routes.TryGetValue(frame.TargetAgentNid, out var baseUrl))
        {
            yield return Fail(frame, "DEMO-UNKNOWN-AGENT", $"no route registered for {frame.TargetAgentNid}");
            yield break;
        }

        // Derive action_id from the DAG node's action URL (nwp://host/action_id).
        var actionId = ExtractActionId(frame.Action);

        var actionFrame = new
        {
            action_id  = actionId,
            @params    = frame.Params,
            request_id = frame.SubtaskId,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/invoke")
        {
            Content = JsonContent.Create(actionFrame, options: Json),
        };
        req.Content.Headers.ContentType = new("application/nwp-frame");

        HttpResponseMessage? resp = null;
        string? httpError = null;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            httpError = ex.Message;
        }

        if (resp is null)
        {
            yield return Fail(frame, "DEMO-HTTP-ERROR", httpError ?? "send failed");
            yield break;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            yield return Fail(frame, "DEMO-UPSTREAM-ERROR", $"{(int)resp.StatusCode}: {body}");
            yield break;
        }

        // CapsFrame shape — pull the first row out of `data[]` as the node's result.
        using var doc = JsonDocument.Parse(body);
        JsonElement? data = null;
        if (doc.RootElement.TryGetProperty("data", out var dataArr) &&
            dataArr.ValueKind == JsonValueKind.Array &&
            dataArr.GetArrayLength() > 0)
        {
            data = dataArr[0].Clone();
        }

        yield return new AlignStreamFrame
        {
            StreamId  = Guid.NewGuid().ToString("D"),
            TaskId    = frame.ParentTaskId,
            SubtaskId = frame.SubtaskId,
            Seq       = 0,
            IsFinal   = true,
            SenderNid = frame.TargetAgentNid,
            Data      = data,
        };
    }

    private static AlignStreamFrame Fail(DelegateFrame frame, string code, string msg) =>
        new()
        {
            StreamId  = Guid.NewGuid().ToString("D"),
            TaskId    = frame.ParentTaskId,
            SubtaskId = frame.SubtaskId,
            Seq       = 0,
            IsFinal   = true,
            SenderNid = frame.TargetAgentNid,
            Error     = new NPS.NOP.Models.StreamError { Code = code, Message = msg, Retryable = false },
        };

    private static string ExtractActionId(string actionUrl)
    {
        // nwp://{host}/{action_id}  or  any URL whose path tail is the action id.
        if (Uri.TryCreate(actionUrl, UriKind.Absolute, out var u))
        {
            var path = u.AbsolutePath.TrimStart('/');
            if (!string.IsNullOrEmpty(path)) return path;
        }
        return actionUrl;
    }
}
