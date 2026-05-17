// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NPS.NOP;

/// <summary>
/// ActivitySource and Meter for NOP orchestration instrumentation.
/// Zero external dependencies — wire OTEL exporters on the host side via
/// <see cref="NopInstrumentation.ActivitySourceName"/> and <see cref="NopInstrumentation.MeterName"/>.
/// All instruments are no-ops when no listener is subscribed.
/// </summary>
internal static class NopTelemetry
{
    internal static readonly ActivitySource Source = new(NopInstrumentation.ActivitySourceName, NopInstrumentation.Version);
    internal static readonly Meter          Meter  = new(NopInstrumentation.MeterName, NopInstrumentation.Version);

    internal static readonly Histogram<double> TaskDurationMs = Meter.CreateHistogram<double>(
        "nps.nop.task.duration_ms", unit: "ms", description: "NOP task total execution duration");
    internal static readonly Histogram<double> NodeDurationMs = Meter.CreateHistogram<double>(
        "nps.nop.node.duration_ms", unit: "ms", description: "NOP DAG node execution duration");
    internal static readonly Counter<long>     NodeRetries    = Meter.CreateCounter<long>(
        "nps.nop.node.retries", unit: "{retries}", description: "NOP DAG node retry attempts");
    internal static readonly Counter<long>     TasksCompleted = Meter.CreateCounter<long>(
        "nps.nop.tasks.completed", unit: "{tasks}", description: "NOP tasks completed successfully");
    internal static readonly Counter<long>     TasksFailed    = Meter.CreateCounter<long>(
        "nps.nop.tasks.failed", unit: "{tasks}", description: "NOP tasks that failed or timed out");
}
