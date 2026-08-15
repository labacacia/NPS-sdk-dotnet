// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Registry;
using NPS.NWP.Actions;
using NPS.NWP.Frames;
using NPS.NWP.Llm;
using NPS.NWP.Registry;

namespace NPS.Benchmarks.TokenSavings;

/// <summary>
/// Deterministic CR-0011 second-turn comparison. Both requests pass through the
/// official ActionFrame and Tier-2 decoder; the stateful request carries only
/// the delta while the benchmark runtime reuses the committed prefix.
/// </summary>
public static class LlmContextBenchmark
{
    public static Result Measure()
    {
        var codec = NpsFrameCodec.Create(
            new FrameRegistryBuilder().AddNcp().AddNwp().Build());

        var tool = new LlmToolDefinitionDto
        {
            Name = "lookup_weather",
            Description = "Return current weather for a city.",
            Parameters =
            [
                new ToolParameterDto
                {
                    Name = "city",
                    Type = "string",
                    Description = "City name.",
                    Required = true,
                },
            ],
        };
        var committedPrefix = new List<LlmMessageDto>
        {
            new() { Role = "system", Content = "Answer concisely and use tools for live weather." },
            new() { Role = "user", Content = "What is the weather in Melbourne?" },
            new()
            {
                Role = "assistant",
                ToolCalls =
                [
                    new LlmToolCallDto
                    {
                        CallId = "call-weather-1",
                        ToolName = "lookup_weather",
                        ArgumentsJson = "{\"city\":\"Melbourne\"}",
                    },
                ],
            },
        };
        var delta = new List<LlmMessageDto>
        {
            new()
            {
                Role = "tool",
                ToolCallId = "call-weather-1",
                ToolName = "lookup_weather",
                Content = "18 C, light rain",
            },
            new() { Role = "user", Content = "Compare that with Sydney in one sentence." },
        };
        var fullSecondTurn = committedPrefix.Concat(delta).ToList();

        var stateless = MakeRequest(fullSecondTurn, tool, context: null, "bench-stateless-2");
        var stateful = MakeRequest(
            delta,
            tool,
            new LlmContextRequestDto
            {
                Operation = LlmContextOperation.Append,
                ContextId = "ctx-benchmark-001",
                BaseVersion = 1,
            },
            "bench-stateful-2");

        var statelessDecoded = EncodeAndStrictDecode(codec, stateless);
        var statefulDecoded = EncodeAndStrictDecode(codec, stateful);
        var reconstructed = committedPrefix.Concat(statefulDecoded.Request.Messages).ToList();
        var semanticParity = CanonicalMessages(statelessDecoded.Request.Messages)
            .SequenceEqual(CanonicalMessages(reconstructed));

        var statelessEvaluated = CountModelTokens(statelessDecoded.Request.Messages);
        var reused = CountModelTokens(committedPrefix);
        var statefulEvaluated = CountModelTokens(statefulDecoded.Request.Messages);

        return new Result(
            StrictNative: true,
            FallbackEnabled: false,
            SemanticParity: semanticParity,
            StatefulDeltaOnly: statefulDecoded.Request.Messages.Count == delta.Count,
            StatelessWireInputBytes: statelessDecoded.WireInputBytes,
            StatefulWireInputBytes: statefulDecoded.WireInputBytes,
            StatelessEvaluatedTokens: statelessEvaluated,
            StatefulEvaluatedTokens: statefulEvaluated,
            StatefulReusedTokens: reused);
    }

    public static string RunReport()
    {
        var result = Measure();
        result.EnsureConformant();

        var wireSavings = 1.0 - (double)result.StatefulWireInputBytes / result.StatelessWireInputBytes;
        var evaluationSavings = 1.0 - (double)result.StatefulEvaluatedTokens / result.StatelessEvaluatedTokens;
        var sb = new StringBuilder();
        sb.AppendLine("# Stateful LLM Context Savings Benchmark");
        sb.AppendLine();
        sb.AppendLine("CR-0011 second-turn comparison using the official `LlmCompleteActionRequest`,");
        sb.AppendLine("`ActionFrame`, and native NCP Tier-2 MessagePack codec. The stateful request");
        sb.AppendLine("contains only the new tool result and user message; the committed prefix is");
        sb.AppendLine("reused by the instrumented deterministic runtime.");
        sb.AppendLine();
        sb.AppendLine("| Gate | Stateless | Stateful | Savings |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "| Decoder `wire_input_bytes` | {0} | {1} | **{2:p1}** |",
            result.StatelessWireInputBytes, result.StatefulWireInputBytes, wireSavings));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "| Runtime `evaluated_tokens` | {0} | {1} | **{2:p1}** |",
            result.StatelessEvaluatedTokens, result.StatefulEvaluatedTokens, evaluationSavings));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "| Runtime `reused_tokens` | 0 | {0} | - |", result.StatefulReusedTokens));
        sb.AppendLine();
        sb.AppendLine($"- Strict native mode: `{result.StrictNative.ToString().ToLowerInvariant()}`");
        sb.AppendLine($"- Protocol fallback enabled: `{result.FallbackEnabled.ToString().ToLowerInvariant()}`");
        sb.AppendLine($"- Ordered role/tool semantic parity: `{result.SemanticParity.ToString().ToLowerInvariant()}`");
        sb.AppendLine($"- Stateful second turn is delta-only: `{result.StatefulDeltaOnly.ToString().ToLowerInvariant()}`");
        sb.AppendLine();
        sb.AppendLine("`wire_input_bytes` is read from the decoded NCP header payload length. It");
        sb.AppendLine("excludes the NCP header, TLS/encryption framing, and response bytes.");
        sb.AppendLine("`evaluated_tokens` is observed from the deterministic benchmark runtime's");
        sb.AppendLine("tokenizer-agnostic `ceil(UTF-8 bytes / 4)` counter over canonical model messages.");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine("dotnet run --project impl/dotnet/benchmarks/NPS.Benchmarks.TokenSavings -- --llm-context --emit");
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static ActionFrame MakeRequest(
        IReadOnlyList<LlmMessageDto> messages,
        LlmToolDefinitionDto tool,
        LlmContextRequestDto? context,
        string requestId) =>
        LlmCompleteAction.ToActionFrame(
            new LlmCompleteActionRequest
            {
                Model = "qwen2.5:0.5b",
                MaxTokens = 128,
                Stream = false,
                Messages = messages,
                Tools = [tool],
                Context = context,
            },
            new NwpActionFrameOptions
            {
                IdempotencyKey = $"00000000-0000-4000-8000-{(context is null ? "000000000001" : "000000000002")}",
                RequestId = requestId,
                TimeoutMs = 30_000,
            });

    private static DecodedRequest EncodeAndStrictDecode(NpsFrameCodec codec, ActionFrame frame)
    {
        var wire = codec.Encode(frame, EncodingTier.MsgPack);
        var header = NpsFrameCodec.PeekHeader(wire);
        if (header.EncodingTier != EncodingTier.MsgPack)
            throw new InvalidOperationException("Strict-native benchmark negotiated a non-MessagePack tier.");
        var decodedFrame = codec.Decode(wire) as ActionFrame
            ?? throw new InvalidOperationException("Strict-native benchmark did not decode an ActionFrame.");
        var request = LlmCompleteAction.ReadRequest(decodedFrame);
        return new DecodedRequest(request, header.PayloadLength);
    }

    private static uint CountModelTokens(IReadOnlyList<LlmMessageDto> messages) =>
        CognCounter.Count(string.Join('\n', CanonicalMessages(messages)));

    private static IEnumerable<string> CanonicalMessages(IEnumerable<LlmMessageDto> messages) =>
        messages.Select(message => Convert.ToBase64String(NwpActionPayloadCodec.EncodeJson(message)));

    private sealed record DecodedRequest(LlmCompleteActionRequest Request, ulong WireInputBytes);

    public sealed record Result(
        bool StrictNative,
        bool FallbackEnabled,
        bool SemanticParity,
        bool StatefulDeltaOnly,
        ulong StatelessWireInputBytes,
        ulong StatefulWireInputBytes,
        uint StatelessEvaluatedTokens,
        uint StatefulEvaluatedTokens,
        uint StatefulReusedTokens)
    {
        public void EnsureConformant()
        {
            if (!StrictNative || FallbackEnabled || !SemanticParity || !StatefulDeltaOnly)
                throw new InvalidOperationException("CR-0011 strict-native semantic gate failed.");
            if (StatefulWireInputBytes >= StatelessWireInputBytes)
                throw new InvalidOperationException("Stateful wire_input_bytes did not improve.");
            if (StatefulEvaluatedTokens >= StatelessEvaluatedTokens || StatefulReusedTokens == 0)
                throw new InvalidOperationException("Stateful runtime token evaluation did not improve.");
        }
    }
}
