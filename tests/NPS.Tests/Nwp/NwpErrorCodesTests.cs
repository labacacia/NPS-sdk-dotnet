// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core;
using NPS.NWP.Http;

namespace NPS.Tests.Nwp;

/// <summary>
/// Sanity-check that NWP error code constants match the spec naming convention
/// (NPS-2 §11): all codes MUST use the NWP-{CATEGORY}-{DETAIL} format.
/// </summary>
public sealed class NwpErrorCodesTests
{
    private static IEnumerable<string> AllErrorCodes()
    {
        yield return NwpErrorCodes.AuthNidScopeViolation;
        yield return NwpErrorCodes.AuthNidExpired;
        yield return NwpErrorCodes.AuthNidRevoked;
        yield return NwpErrorCodes.AuthNidUntrustedIssuer;
        yield return NwpErrorCodes.AuthNidCapabilityMissing;
        yield return NwpErrorCodes.QueryFilterInvalid;
        yield return NwpErrorCodes.QueryFieldUnknown;
        yield return NwpErrorCodes.QueryCursorInvalid;
        yield return NwpErrorCodes.ActionNotFound;
        yield return NwpErrorCodes.ActionParamsInvalid;
        yield return NwpErrorCodes.ActionIdempotencyConflict;
        yield return NwpErrorCodes.TaskNotFound;
        yield return NwpErrorCodes.TaskAlreadyCancelled;
        yield return NwpErrorCodes.BudgetExceeded;
        yield return NwpErrorCodes.DepthExceeded;
        yield return NwpErrorCodes.GraphCycle;
        yield return NwpErrorCodes.NodeUnavailable;
        yield return NwpErrorCodes.ManifestVersionUnsupported;
    }

    [Fact]
    public void AllNwpErrorCodes_HaveNwpPrefix()
    {
        foreach (var code in AllErrorCodes())
            Assert.StartsWith("NWP-", code);
    }

    [Fact]
    public void AllNwpErrorCodes_AreUpperCase()
    {
        foreach (var code in AllErrorCodes())
            Assert.Equal(code, code.ToUpperInvariant());
    }

    [Fact]
    public void AllNwpErrorCodes_HaveAtLeastTwoSegments()
    {
        // Minimum: "NWP-{CAT}-{DETAIL}" → 3 segments when split by '-'
        foreach (var code in AllErrorCodes())
            Assert.True(code.Split('-').Length >= 3,
                $"Error code '{code}' must have at least 3 dash-separated segments.");
    }

    // ── NPS status code format ───────────────────────────────────────────────

    [Fact]
    public void NpsStatusCodes_AllCodes_HaveNpsPrefix()
    {
        var codes = new[]
        {
            NpsStatusCodes.Ok,
            NpsStatusCodes.OkAccepted,
            NpsStatusCodes.OkNoContent,
            NpsStatusCodes.ClientBadFrame,
            NpsStatusCodes.ClientBadParam,
            NpsStatusCodes.ClientNotFound,
            NpsStatusCodes.ClientConflict,
            NpsStatusCodes.ClientGone,
            NpsStatusCodes.ClientUnprocessable,
            NpsStatusCodes.AuthUnauthenticated,
            NpsStatusCodes.AuthForbidden,
            NpsStatusCodes.LimitRate,
            NpsStatusCodes.LimitBudget,
            NpsStatusCodes.LimitPayload,
            NpsStatusCodes.ServerInternal,
            NpsStatusCodes.ServerUnavailable,
            NpsStatusCodes.ServerTimeout,
            NpsStatusCodes.ServerEncodingUnsupported,
            NpsStatusCodes.StreamSeqGap,
            NpsStatusCodes.StreamNotFound,
            NpsStatusCodes.StreamLimit,
        };

        foreach (var code in codes)
            Assert.StartsWith("NPS-", code);
    }
}
