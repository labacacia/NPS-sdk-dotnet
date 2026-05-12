// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace NPS.Daemon.Observability.Logging;

/// <summary>
/// Wires single-line JSON console logging keyed off the <c>NPS_LOG_LEVEL</c>
/// environment variable (trace / debug / information / warning / error /
/// critical / none — case-insensitive). Each record carries the four fields
/// the operator runbook expects: <c>timestamp</c>, <c>level</c>, <c>msg</c>,
/// <c>trace_id</c>.
/// </summary>
public static class JsonStructuredLogging
{
    /// <summary>Env var that overrides the default minimum log level.</summary>
    public const string LogLevelEnvVar = "NPS_LOG_LEVEL";

    /// <summary>
    /// Replaces the host's default console logger with the JSON formatter and
    /// honours <c>NPS_LOG_LEVEL</c>.
    /// </summary>
    /// <param name="builder">The logging builder from the host.</param>
    /// <param name="defaultLevel">
    /// Fallback level when the env var is unset or unparseable. Defaults to
    /// <see cref="LogLevel.Information"/>.
    /// </param>
    public static ILoggingBuilder AddNpsJsonConsole(
        this ILoggingBuilder builder,
        LogLevel defaultLevel = LogLevel.Information)
    {
        builder.ClearProviders();

        var level = ResolveLogLevel(defaultLevel);
        builder.SetMinimumLevel(level);

        builder.AddConsoleFormatter<NpsJsonConsoleFormatter, ConsoleFormatterOptions>(opts =>
        {
            opts.IncludeScopes = true;
        });
        builder.AddConsole(opts =>
        {
            opts.FormatterName = NpsJsonConsoleFormatter.FormatterName;
        });

        return builder;
    }

    /// <summary>Resolves the configured level, falling back to <paramref name="fallback"/>.</summary>
    public static LogLevel ResolveLogLevel(LogLevel fallback)
    {
        var raw = Environment.GetEnvironmentVariable(LogLevelEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return Enum.TryParse<LogLevel>(raw, ignoreCase: true, out var lvl) ? lvl : fallback;
    }
}

/// <summary>
/// Single-line JSON console formatter for NPS daemons. Emits one record per
/// log event with <c>timestamp</c>, <c>level</c>, <c>msg</c>, <c>logger</c>,
/// <c>trace_id</c>, and <c>exception</c> when present.
/// </summary>
internal sealed class NpsJsonConsoleFormatter : ConsoleFormatter, IDisposable
{
    public const string FormatterName = "nps-json";

    public NpsJsonConsoleFormatter(IOptionsMonitor<ConsoleFormatterOptions> _) : base(FormatterName) { }

    public void Dispose() { }

    public override void Write<TState>(
        in LogEntry<TState> entry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var msg = entry.Formatter?.Invoke(entry.State, entry.Exception);
        if (msg is null && entry.Exception is null) return;

        using var ms = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("timestamp", DateTime.UtcNow.ToString("o"));
            w.WriteString("level",     LevelName(entry.LogLevel));
            w.WriteString("msg",       msg ?? string.Empty);
            w.WriteString("logger",    entry.Category);

            var traceId = Activity.Current?.TraceId.ToHexString();
            if (!string.IsNullOrEmpty(traceId))
                w.WriteString("trace_id", traceId);

            if (entry.EventId.Id != 0 || !string.IsNullOrEmpty(entry.EventId.Name))
            {
                w.WriteStartObject("event");
                w.WriteNumber("id", entry.EventId.Id);
                if (!string.IsNullOrEmpty(entry.EventId.Name))
                    w.WriteString("name", entry.EventId.Name);
                w.WriteEndObject();
            }

            if (entry.Exception is { } ex)
                w.WriteString("exception", ex.ToString());

            w.WriteEndObject();
            w.Flush();
        }

        textWriter.Write(Encoding.UTF8.GetString(ms.ToArray()));
        textWriter.Write(Environment.NewLine);
    }

    private static string LevelName(LogLevel l) => l switch
    {
        LogLevel.Trace       => "trace",
        LogLevel.Debug       => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning     => "warn",
        LogLevel.Error       => "error",
        LogLevel.Critical    => "critical",
        _                    => "none",
    };
}
