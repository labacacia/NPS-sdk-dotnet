// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP;

/// <summary>
/// Public instrument-name constants for the NOP layer.
/// Use these when configuring OpenTelemetry exporters:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t =&gt; t.AddSource(NopInstrumentation.ActivitySourceName))
///     .WithMetrics(m =&gt; m.AddMeter(NopInstrumentation.MeterName));
/// </code>
/// </summary>
public static class NopInstrumentation
{
    /// <summary>ActivitySource name for NOP orchestration spans.</summary>
    public const string ActivitySourceName = "nps.nop";

    /// <summary>Meter name for NOP orchestration metrics.</summary>
    public const string MeterName = "nps.nop";

    internal const string Version = "1.0.0";
}
