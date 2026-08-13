// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Registry;
using NPS.NDP.Frames;
using NPS.NDP.Registry;

var registry = new FrameRegistryBuilder()
    .AddNcp()
    .AddNdp()
    .Build();
var codec = NpsFrameCodec.Create(registry);

foreach (ulong? clusterEpoch in new ulong?[] { null, 7UL })
{
    var frame = new AnnounceFrame
    {
        Nid = "urn:nps:node:example.test:aot-smoke",
        NodeType = "action",
        Addresses =
        [
            new NdpAddress
            {
                Host = "127.0.0.1",
                Port = 17433,
                Protocol = "ncp",
            },
        ],
        Capabilities = ["llm:complete"],
        Ttl = 60,
        Timestamp = "2026-08-13T00:00:00Z",
        Signature = "ed25519:test",
        ClusterAnchor = "urn:nps:node:example.test:aot-smoke",
        ClusterEpoch = clusterEpoch,
    };

    var decoded = codec.Decode(codec.Encode(frame, EncodingTier.MsgPack)) as AnnounceFrame
        ?? throw new InvalidOperationException("NativeAOT frame round-trip returned the wrong type.");

    if (decoded.ClusterEpoch != clusterEpoch)
    {
        throw new InvalidOperationException(
            $"NativeAOT nullable UInt64 mismatch: expected {clusterEpoch}, got {decoded.ClusterEpoch}.");
    }
}

Console.WriteLine("NPS NativeAOT codec smoke passed.");
