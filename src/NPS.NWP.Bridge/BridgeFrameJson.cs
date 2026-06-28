// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

internal static class BridgeFrameJson
{
    public static string Serialize(IFrame frame) =>
        JsonSerializer.Serialize(ToElement(frame), BridgeNodeMiddleware.Json);

    public static JsonElement ToElement(IFrame frame) => frame switch
    {
        CapsFrame caps => JsonSerializer.SerializeToElement(caps, BridgeNodeMiddleware.Json),
        ErrorFrame error => JsonSerializer.SerializeToElement(error, BridgeNodeMiddleware.Json),
        ActionFrame action => JsonSerializer.SerializeToElement(action, BridgeNodeMiddleware.Json),
        _ => JsonSerializer.SerializeToElement(frame, frame.GetType(), BridgeNodeMiddleware.Json),
    };
}
