// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP;

/// <summary>
/// Public instrument-name constants for the NWP layer.
/// Use these when configuring OpenTelemetry exporters:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t =&gt; t.AddSource(NwpInstrumentation.ActivitySourceName))
///     .WithMetrics(m =&gt; m.AddMeter(NwpInstrumentation.MeterName));
/// </code>
/// </summary>
public static class NwpInstrumentation
{
    /// <summary>ActivitySource name for NWP frame processing spans.</summary>
    public const string ActivitySourceName = "nps.nwp";

    /// <summary>Meter name for NWP frame metrics.</summary>
    public const string MeterName = "nps.nwp";

    internal const string Version = "1.0.0";
}
