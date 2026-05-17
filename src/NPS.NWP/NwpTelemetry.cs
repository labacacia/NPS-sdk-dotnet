// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NPS.NWP;

/// <summary>
/// ActivitySource and Meter for NWP frame processing instrumentation.
/// Zero external dependencies — wire OTEL exporters on the host side via
/// <see cref="NwpInstrumentation.ActivitySourceName"/> and <see cref="NwpInstrumentation.MeterName"/>.
/// All instruments are no-ops when no listener is subscribed.
/// </summary>
internal static class NwpTelemetry
{
    internal static readonly ActivitySource Source = new(NwpInstrumentation.ActivitySourceName, NwpInstrumentation.Version);
    internal static readonly Meter          Meter  = new(NwpInstrumentation.MeterName, NwpInstrumentation.Version);

    internal static readonly Counter<long>     FramesProcessed = Meter.CreateCounter<long>(
        "nps.frames.processed", unit: "{frames}", description: "Total NWP frames processed");
    internal static readonly Histogram<double> FrameDurationMs = Meter.CreateHistogram<double>(
        "nps.frames.processing_ms", unit: "ms", description: "NWP frame processing duration");
    internal static readonly Counter<long>     CgnConsumed     = Meter.CreateCounter<long>(
        "nps.cgn.consumed", unit: "{cgn}", description: "CGN units consumed in NWP responses");
    internal static readonly Counter<long>     FrameErrors     = Meter.CreateCounter<long>(
        "nps.frames.errors", unit: "{frames}", description: "NWP frames that returned an error response");
}
