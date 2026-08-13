// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using NPS.NWP.Actions;
using NPS.NWP.Llm;
using NPS.NWP.Http;
using NPS.Core;

namespace NPS.Tests.Nwp;

public sealed class LlmContextContractTests
{
    [Fact]
    public void ContextErrorAndStatusConstants_AreCanonical()
    {
        Assert.Equal("NWP-LLM-CONTEXT-EXPIRED", NwpErrorCodes.LlmContextExpired);
        Assert.Equal("NPS-LIMIT-RESOURCE", NpsStatusCodes.LimitResource);
    }

    [Fact]
    public void StatefulCompletion_RoundTripsCanonicalContextAndReceipt()
    {
        var request = new LlmCompleteActionRequest
        {
            Model = "willow-small",
            Messages = [new LlmMessageDto { Role = "user", Content = "Hello" }],
            Context = new LlmContextRequestDto
            {
                Operation = LlmContextOperation.Create,
                TtlSeconds = 600,
            },
        };

        var json = Encoding.UTF8.GetString(NwpActionPayloadCodec.EncodeJson(request));
        Assert.Contains("\"operation\":\"create\"", json);
        Assert.Contains("\"ttl_seconds\":600", json);

        var decoded = NwpActionPayloadCodec.DecodeMsgPack<LlmCompleteActionRequest>(
            NwpActionPayloadCodec.EncodeMsgPack(request));
        Assert.Equal(LlmContextOperation.Create, decoded.Context!.Operation);

        var response = new LlmCompleteActionResponse
        {
            StopReason = LlmStopReason.EndTurn,
            Content = "Hi",
            Usage = new LlmUsageDto { WireInputBytes = 384 },
            Context = new LlmContextReceiptDto
            {
                ContextId = "AQIDBAUGBwgJCgsMDQ4PEA",
                Version = 1,
                Operation = LlmContextOperation.Create,
                State = LlmContextState.Active,
                ExpiresAt = "2026-08-12T01:00:00Z",
            },
        };

        var roundTrip = NwpActionPayloadCodec.DecodeJson<LlmCompleteActionResponse>(
            NwpActionPayloadCodec.EncodeJson(response));
        Assert.Equal(384ul, roundTrip.Usage!.WireInputBytes);
        Assert.Equal(LlmContextState.Active, roundTrip.Context!.State);
    }

    [Fact]
    public void LifecycleActions_UseCanonicalIdsAndPayloads()
    {
        var status = LlmContextActions.ToStatusActionFrame(new LlmContextStatusRequestDto
        {
            IdempotencyKey = "00000000-0000-4000-8000-000000000010",
        });
        Assert.Equal(LlmContextActions.StatusActionId, status.ActionId);
        Assert.Equal(
            "00000000-0000-4000-8000-000000000010",
            LlmContextActions.ReadStatusRequest(status).IdempotencyKey);

        var release = LlmContextActions.ToReleaseActionFrame(
            new LlmContextReleaseRequestDto
            {
                ContextId = "AQIDBAUGBwgJCgsMDQ4PEA",
                BaseVersion = 7,
            },
            new NwpActionFrameOptions { IdempotencyKey = "release-1" });
        Assert.Equal(LlmContextActions.ReleaseActionId, release.ActionId);
        Assert.Equal(7ul, LlmContextActions.ReadReleaseRequest(release).BaseVersion);
        Assert.Equal("release-1", release.IdempotencyKey);
    }
}
