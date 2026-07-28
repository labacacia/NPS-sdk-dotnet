// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>
/// Canonical NWP frame ⇄ JSON projection shared by the Bridge's outbound dispatchers, its inbound
/// servers, and the ASP.NET hosting middleware — one wire shape, defined once.
/// </summary>
public static class BridgeFrameJson
{
    /// <summary>Serializer options producing the canonical NWP-over-JSON shape.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Serialize a frame to its canonical JSON text.</summary>
    public static string Serialize(IFrame frame) => JsonSerializer.Serialize(ToElement(frame), Json);

    /// <summary>Project a frame onto a canonical JSON element.</summary>
    public static JsonElement ToElement(IFrame frame) => frame switch
    {
        CapsFrame caps => JsonSerializer.SerializeToElement(caps, Json),
        ErrorFrame error => JsonSerializer.SerializeToElement(error, Json),
        ActionFrame action => JsonSerializer.SerializeToElement(action, Json),
        _ => JsonSerializer.SerializeToElement(frame, frame.GetType(), Json),
    };
}
