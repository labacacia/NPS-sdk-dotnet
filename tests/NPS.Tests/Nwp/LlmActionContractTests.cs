// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Actions;
using NPS.NWP.Frames;
using NPS.NWP.Llm;

namespace NPS.Tests.Nwp;

public sealed class LlmActionContractTests
{
    [Fact]
    public void LlmCompleteRequest_JsonPayload_UsesCanonicalSnakeCase()
    {
        var request = MakeRequest();

        var json = Encoding.UTF8.GetString(NwpActionPayloadCodec.EncodeJson(request));

        Assert.Contains("\"kind\":\"llm.complete\"", json);
        Assert.Contains("\"max_tokens\":4096", json);
        Assert.Contains("\"tool_call_id\":\"tool-1\"", json);
        Assert.Contains("\"tool_calls\"", json);
        Assert.DoesNotContain("MaxTokens", json);
        Assert.DoesNotContain("ToolCallId", json);

        var decoded = NwpActionPayloadCodec.DecodeJson<LlmCompleteActionRequest>(Encoding.UTF8.GetBytes(json));
        Assert.Equal("gpt-test", decoded.Model);
        Assert.Equal(4096u, decoded.MaxTokens);
        Assert.False(decoded.Stream);
        Assert.Equal("tool-1", decoded.Messages[2].ToolCallId);
    }

    [Fact]
    public void LlmCompleteRequest_MsgPackPayload_RoundTripsWithSnakeCaseKeys()
    {
        var request = MakeRequest() with { Stream = true };

        var bytes = NwpActionPayloadCodec.EncodeMsgPack(request);
        var decoded = NwpActionPayloadCodec.DecodeMsgPack<LlmCompleteActionRequest>(bytes);

        Assert.Equal(LlmCompleteAction.ActionId, decoded.Kind);
        Assert.True(decoded.Stream);
        Assert.Equal("weather.lookup", decoded.Tools![0].Name);
        Assert.Equal("location", decoded.Tools[0].Parameters![0].Name);
    }

    [Fact]
    public void LlmCompleteRequest_DecoderAcceptsPascalCaseCompatibilityPayload()
    {
        const string json = """
        {
          "Kind": "llm.complete",
          "Model": "gpt-test",
          "MaxTokens": 2048,
          "Stream": false,
          "Messages": [
            {
              "Role": "assistant",
              "Content": "Calling a tool.",
              "ToolCalls": [
                {
                  "CallId": "call-1",
                  "ToolName": "weather.lookup",
                  "ArgumentsJson": "{\"location\":\"Melbourne\"}"
                }
              ]
            },
            {
              "Role": "tool",
              "ToolCallId": "tool-1",
              "ToolName": "weather.lookup",
              "Content": "{\"temperature\":21}"
            }
          ]
        }
        """;

        var decoded = NwpActionPayloadCodec.DecodeJson<LlmCompleteActionRequest>(
            Encoding.UTF8.GetBytes(json));

        Assert.Equal(2048u, decoded.MaxTokens);
        Assert.Equal("call-1", decoded.Messages[0].ToolCalls![0].CallId);
        Assert.Equal("tool-1", decoded.Messages[1].ToolCallId);
    }

    [Fact]
    public void LlmCompleteAction_MapsToAndFromActionFrameParams()
    {
        var requestId = Guid.NewGuid().ToString();
        var frame = LlmCompleteAction.ToActionFrame(
            MakeRequest(),
            new NwpActionFrameOptions
            {
                RequestId = requestId,
                TimeoutMs = 30_000,
                Priority = "normal",
            });

        Assert.Equal(LlmCompleteAction.ActionId, frame.ActionId);
        Assert.Equal(requestId, frame.RequestId);
        Assert.Equal(30_000u, frame.TimeoutMs);
        Assert.False(frame.Async);
        Assert.NotNull(frame.Params);
        Assert.Equal(LlmCompleteAction.ActionId, frame.Params!.Value.GetProperty("kind").GetString());
        Assert.Equal(4096u, frame.Params.Value.GetProperty("max_tokens").GetUInt32());

        Assert.True(LlmCompleteAction.TryReadRequest(frame, out var decoded, out var error), error);
        Assert.Null(error);
        Assert.Equal("gpt-test", decoded!.Model);
        Assert.Equal(3, decoded.Messages.Count);
    }

    [Fact]
    public void LlmCompleteResponse_JsonPayload_UsesStopReasonAndToolCalls()
    {
        var response = new LlmCompleteActionResponse
        {
            StopReason = LlmStopReason.ToolCalls,
            Content = "Need tool output.",
            ToolCalls =
            [
                new LlmToolCallDto
                {
                    CallId = "call-1",
                    ToolName = "weather.lookup",
                    ArgumentsJson = "{\"location\":\"Melbourne\"}",
                },
            ],
        };

        var json = Encoding.UTF8.GetString(NwpActionPayloadCodec.EncodeJson(response));

        Assert.Contains("\"stop_reason\":\"tool_calls\"", json);
        Assert.Contains("\"call_id\":\"call-1\"", json);
        Assert.DoesNotContain("StopReason", json);

        var element = LlmCompleteAction.ToResponsePayload(response);
        var decoded = LlmCompleteAction.ReadResponsePayload(element);
        Assert.Equal(LlmStopReason.ToolCalls, decoded.StopReason);
        Assert.Equal("weather.lookup", decoded.ToolCalls![0].ToolName);
    }

    [Fact]
    public void LlmCompleteResponse_MapsToAndFromCapsFrameData()
    {
        var response = new LlmCompleteActionResponse
        {
            StopReason = LlmStopReason.EndTurn,
            Content = "Done.",
        };

        var frame = LlmCompleteAction.ToCapsFrame(response, tokenEst: 3, tokenizerUsed: "test-tokenizer");

        Assert.Equal(LlmCompleteAction.ResponseAnchorRef, frame.AnchorRef);
        Assert.Equal(1u, frame.Count);
        Assert.Equal(3u, frame.TokenEst);
        Assert.Equal("test-tokenizer", frame.TokenizerUsed);
        Assert.Equal("end_turn", frame.Data[0].GetProperty("stop_reason").GetString());

        var decoded = LlmCompleteAction.ReadResponse(frame);
        Assert.Equal(LlmStopReason.EndTurn, decoded.StopReason);
        Assert.Equal("Done.", decoded.Content);
    }

    [Fact]
    public void LlmCompleteStreamChunk_MapsToAndFromStreamFrameData()
    {
        var frame = LlmCompleteAction.ToStreamFrame(
            "stream-1",
            seq: 0,
            isLast: false,
            [
                new LlmCompleteStreamChunkDto
                {
                    ContentDelta = "Hel",
                },
                new LlmCompleteStreamChunkDto
                {
                    ContentDelta = "lo",
                    StopReason = LlmStopReason.EndTurn,
                },
            ],
            includeAnchorRef: true,
            windowSize: 8);

        Assert.Equal("stream-1", frame.StreamId);
        Assert.Equal(0u, frame.Seq);
        Assert.False(frame.IsLast);
        Assert.Equal(LlmCompleteAction.StreamAnchorRef, frame.AnchorRef);
        Assert.Equal(8u, frame.WindowSize);
        Assert.Equal("Hel", frame.Data[0].GetProperty("content_delta").GetString());
        Assert.Equal("end_turn", frame.Data[1].GetProperty("stop_reason").GetString());

        var decoded = LlmCompleteAction.ReadStreamChunks(frame);
        Assert.Equal(2, decoded.Count);
        Assert.Equal("Hel", decoded[0].ContentDelta);
        Assert.Equal(LlmStopReason.EndTurn, decoded[1].StopReason);
    }

    [Fact]
    public void FramePayloadCodec_ReportsCapsFrameDataIndexErrors()
    {
        var frame = LlmCompleteAction.ToCapsFrame(new LlmCompleteActionResponse
        {
            StopReason = LlmStopReason.EndTurn,
            Content = "Done.",
        });

        Assert.False(NwpFramePayloadCodec.TryReadCapsPayload<LlmCompleteActionResponse>(
            frame,
            out _,
            out var error,
            index: 2));
        Assert.Contains("outside the payload range", error);
    }

    [Fact]
    public void LlmCompleteResponse_MapsToAndFromAsyncTaskStatusResult()
    {
        var status = new ActionTaskStatus
        {
            TaskId = "task-1",
            Status = "completed",
            Progress = 1.0,
            CreatedAt = "2026-07-04T00:00:00Z",
            UpdatedAt = "2026-07-04T00:00:01Z",
            Result = NwpFramePayloadCodec.ToJsonElement(new LlmCompleteActionResponse
            {
                StopReason = LlmStopReason.EndTurn,
                Content = "Done.",
            }),
        };

        var decoded = LlmCompleteAction.ReadAsyncResult(status);
        Assert.Equal(LlmStopReason.EndTurn, decoded.StopReason);
        Assert.Equal("Done.", decoded.Content);
    }

    [Fact]
    public void FramePayloadCodec_RejectsTaskResultBeforeCompletion()
    {
        var status = new ActionTaskStatus
        {
            TaskId = "task-1",
            Status = "running",
            CreatedAt = "2026-07-04T00:00:00Z",
            UpdatedAt = "2026-07-04T00:00:01Z",
            Result = NwpFramePayloadCodec.ToJsonElement(new LlmCompleteActionResponse
            {
                StopReason = LlmStopReason.EndTurn,
                Content = "Done.",
            }),
        };

        Assert.False(NwpFramePayloadCodec.TryReadTaskResult<LlmCompleteActionResponse>(
            status,
            out _,
            out var error));
        Assert.Contains("not 'completed'", error);
    }

    [Fact]
    public void FramePayloadCodec_ReadsErrorFrameDetails()
    {
        var frame = new ErrorFrame
        {
            Status = "NPS-CLIENT-BAD-FRAME",
            Error = "NWP-ACTION-PARAMS-INVALID",
            Message = "Invalid LLM payload.",
            Details = NwpFramePayloadCodec.ToJsonElement(new LlmErrorDetails
            {
                Field = "messages",
                Reason = "must not be empty",
            }),
        };

        var details = NwpFramePayloadCodec.ReadErrorDetails<LlmErrorDetails>(frame);
        Assert.Equal("messages", details.Field);
        Assert.Equal("must not be empty", details.Reason);
    }

    [Fact]
    public void LlmCompleteAction_RejectsWrongActionIdOrPayloadKind()
    {
        var wrongAction = new ActionFrame
        {
            ActionId = "model.complete",
            Params = NwpActionPayloadCodec.ToJsonElement(MakeRequest()),
        };

        Assert.False(LlmCompleteAction.TryReadRequest(wrongAction, out _, out var actionError));
        Assert.Contains("Unexpected action_id", actionError);

        var wrongKind = new ActionFrame
        {
            ActionId = LlmCompleteAction.ActionId,
            Params = NwpActionPayloadCodec.ToJsonElement(MakeRequest() with { Kind = "model.complete" }),
        };

        Assert.False(LlmCompleteAction.TryReadRequest(wrongKind, out _, out var kindError));
        Assert.Contains("does not match", kindError);
    }

    private static LlmCompleteActionRequest MakeRequest() => new()
    {
        Model = "gpt-test",
        MaxTokens = 4096,
        Stream = false,
        Messages =
        [
            new LlmMessageDto
            {
                Role = "system",
                Content = "Answer tersely.",
            },
            new LlmMessageDto
            {
                Role = "assistant",
                Content = "I can call a tool.",
                ToolCalls =
                [
                    new LlmToolCallDto
                    {
                        CallId = "call-1",
                        ToolName = "weather.lookup",
                        ArgumentsJson = "{\"location\":\"Melbourne\"}",
                    },
                ],
            },
            new LlmMessageDto
            {
                Role = "tool",
                ToolCallId = "tool-1",
                ToolName = "weather.lookup",
                Content = "{\"temperature\":21}",
            },
        ],
        Tools =
        [
            new LlmToolDefinitionDto
            {
                Name = "weather.lookup",
                Description = "Look up weather by city.",
                Parameters =
                [
                    new ToolParameterDto
                    {
                        Name = "location",
                        Type = "string",
                        Description = "City name.",
                        Required = true,
                    },
                ],
            },
        ],
    };

    private sealed record LlmErrorDetails
    {
        public required string Field { get; init; }

        public required string Reason { get; init; }
    }
}
