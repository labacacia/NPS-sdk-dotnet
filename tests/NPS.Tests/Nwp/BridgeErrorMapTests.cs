// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core;
using NPS.NWP.Bridge;
using Xunit;

namespace NPS.Tests.Nwp;

/// <summary>
/// NWP §16.3 — the normative NPS ↔ foreign-protocol error mapping (NPS-CR-0010).
/// One implementation serves both directions and all three protocols; these vectors pin it.
/// </summary>
public class BridgeErrorMapTests
{
    [Theory]
    [InlineData(NpsStatusCodes.ClientBadFrame, BridgeJsonRpcErrorCodes.InvalidRequest)]
    [InlineData(NpsStatusCodes.ClientBadParam, BridgeJsonRpcErrorCodes.InvalidParams)]
    [InlineData(NpsStatusCodes.ClientUnprocessable, BridgeJsonRpcErrorCodes.InvalidParams)]
    [InlineData(NpsStatusCodes.ClientConflict, BridgeJsonRpcErrorCodes.Conflict)]
    [InlineData(NpsStatusCodes.AuthUnauthenticated, BridgeJsonRpcErrorCodes.Unauthenticated)]
    [InlineData(NpsStatusCodes.AuthForbidden, BridgeJsonRpcErrorCodes.Forbidden)]
    [InlineData(NpsStatusCodes.LimitBudget, BridgeJsonRpcErrorCodes.LimitExceeded)]
    [InlineData(NpsStatusCodes.ServerUnsupported, BridgeJsonRpcErrorCodes.MethodNotFound)]
    [InlineData(NpsStatusCodes.ServerInternal, BridgeJsonRpcErrorCodes.InternalError)]
    public void NpsStatus_MapsToJsonRpc(string status, int expected) =>
        Assert.Equal(expected, BridgeErrorMap.ToJsonRpc(status));

    [Fact]
    public void UnknownTool_IsMethodNotFound_ButUnknownResource_IsInvalidParams()
    {
        // §16.3 calls this distinction out: an unknown *tool* is a missing method to an MCP
        // client; an unknown *resource* is a bad argument.
        Assert.Equal(BridgeJsonRpcErrorCodes.MethodNotFound,
            BridgeErrorMap.ToJsonRpc(NpsStatusCodes.ClientNotFound));
        Assert.Equal(BridgeJsonRpcErrorCodes.InvalidParams,
            BridgeErrorMap.ToJsonRpc(NpsStatusCodes.ClientNotFound, resourceRead: true));
    }

    [Fact]
    public void AuthClasses_AreNotCollapsed()
    {
        // The defect in compat/grpc-ingress: 401 and 403 both became PERMISSION_DENIED.
        Assert.Equal("UNAUTHENTICATED", BridgeErrorMap.ToGrpcStatus(NpsStatusCodes.AuthUnauthenticated));
        Assert.Equal("PERMISSION_DENIED", BridgeErrorMap.ToGrpcStatus(NpsStatusCodes.AuthForbidden));
        Assert.NotEqual(
            BridgeErrorMap.ToJsonRpc(NpsStatusCodes.AuthUnauthenticated),
            BridgeErrorMap.ToJsonRpc(NpsStatusCodes.AuthForbidden));
    }

    [Fact]
    public void ServerClasses_AreNotAllCollapsedOntoUnavailable()
    {
        // The other compat/grpc-ingress defect: every 5xx became UNAVAILABLE.
        Assert.Equal("INTERNAL", BridgeErrorMap.ToGrpcStatus(NpsStatusCodes.ServerInternal));
        Assert.Equal("UNAVAILABLE", BridgeErrorMap.ToGrpcStatus(NpsStatusCodes.ServerUnavailable));
        Assert.Equal("UNIMPLEMENTED", BridgeErrorMap.ToGrpcStatus(NpsStatusCodes.ServerUnsupported));
        Assert.Equal("DEADLINE_EXCEEDED", BridgeErrorMap.ToGrpcStatus(NpsStatusCodes.ServerTimeout));
    }

    [Theory]
    [InlineData(401, NpsStatusCodes.AuthUnauthenticated)]
    [InlineData(403, NpsStatusCodes.AuthForbidden)]
    [InlineData(404, NpsStatusCodes.ClientNotFound)]
    [InlineData(409, NpsStatusCodes.ClientConflict)]
    [InlineData(429, NpsStatusCodes.LimitRate)]
    [InlineData(501, NpsStatusCodes.ServerUnsupported)]
    [InlineData(502, NpsStatusCodes.DownstreamUnavailable)]
    public void HttpStatus_MapsBackToMostSpecificNpsStatus(int http, string expected) =>
        Assert.Equal(expected, BridgeErrorMap.FromHttpStatus(http));

    [Fact]
    public void AuthAndLimitFailures_MustSurfaceAsProtocolErrors()
    {
        Assert.True(BridgeErrorMap.MustBeProtocolError(NpsStatusCodes.AuthForbidden));
        Assert.True(BridgeErrorMap.MustBeProtocolError(NpsStatusCodes.LimitRate));
        Assert.True(BridgeErrorMap.MustBeProtocolError(NpsStatusCodes.ServerUnsupported));
        // A domain failure stays an isError result — that is what MCP's flag is for.
        Assert.False(BridgeErrorMap.MustBeProtocolError(NpsStatusCodes.ClientUnprocessable));
    }
}
