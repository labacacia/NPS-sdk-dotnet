// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.Bridge;

/// <summary>
/// Built-in Bridge dispatcher for A2A JSON-RPC endpoints over HTTP POST.
/// </summary>
public sealed class A2aBridgeDispatcher : JsonRpcBridgeDispatcher
{
    /// <summary>Anchor reference used for A2A bridge response records.</summary>
    public const string ResponseAnchorRef = "nps://bridge/a2a-jsonrpc-response/v1";

    /// <summary>Create an A2A bridge dispatcher over an existing client.</summary>
    public A2aBridgeDispatcher(HttpClient client)
        : base(client, "tasks/send", ResponseAnchorRef)
    {
    }

    /// <inheritdoc />
    public override string Protocol => BridgeProtocols.A2a;
}
