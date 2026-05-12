// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace NPS.Daemon.Observability.Metrics;

/// <summary>
/// Lightweight Prometheus-compatible counter / gauge registry. Backs the
/// <c>/metrics</c> endpoint. Designed for the daemon use case where the metric
/// set is small (&lt;~30) and known up-front; we avoid the full
/// <c>System.Diagnostics.Metrics</c> pipeline so the output is deterministic
/// and bypasses the OTEL collector chain that operators may not have wired.
/// </summary>
public sealed class MetricsRegistry
{
    private readonly List<MetricEntry> _entries = new();
    private readonly object            _gate    = new();

    /// <summary>Registers a counter (monotonic, accumulates per labelset).</summary>
    public Counter RegisterCounter(string name, string help, params string[] labelNames)
    {
        var c = new Counter(name, help, labelNames);
        lock (_gate) _entries.Add(c);
        return c;
    }

    /// <summary>Registers a gauge (sampled, may go up and down).</summary>
    public Gauge RegisterGauge(string name, string help)
    {
        var g = new Gauge(name, help);
        lock (_gate) _entries.Add(g);
        return g;
    }

    /// <summary>Writes the registry to <paramref name="sb"/> in Prometheus exposition format.</summary>
    public void WriteTo(StringBuilder sb)
    {
        MetricEntry[] snap;
        lock (_gate) snap = _entries.ToArray();
        foreach (var e in snap) e.WriteTo(sb);
    }

    public abstract class MetricEntry
    {
        public abstract void WriteTo(StringBuilder sb);
    }

    /// <summary>Monotonic counter; one cell per label-value tuple.</summary>
    public sealed class Counter : MetricEntry
    {
        // ASCII Unit Separator — safe in label values (Prometheus label
        // values are arbitrary UTF-8, but operators rarely use control chars).
        private const char CellSeparator = '';

        private readonly string                                     _name;
        private readonly string                                     _help;
        private readonly string[]                                   _labels;
        private readonly ConcurrentDictionary<string, CounterValue> _cells = new();

        internal Counter(string name, string help, string[] labels)
        {
            _name   = name;
            _help   = help;
            _labels = labels;
            if (labels.Length == 0) _cells[""] = new CounterValue();
        }

        /// <summary>Increment by 1 with no labels.</summary>
        public void Inc() => Inc(1, Array.Empty<string>());

        /// <summary>Increment by <paramref name="by"/> with no labels.</summary>
        public void Inc(double by) => Inc(by, Array.Empty<string>());

        /// <summary>
        /// Increment a labelled cell by 1. <paramref name="labelValues"/> must
        /// match the registered label order; missing label values count as
        /// the empty string.
        /// </summary>
        public void Inc(params string[] labelValues) => Inc(1, labelValues);

        public void Inc(double by, params string[] labelValues)
        {
            var key  = CellKey(labelValues);
            var cell = _cells.GetOrAdd(key, _ => new CounterValue());
            cell.Add(by);
        }

        public override void WriteTo(StringBuilder sb)
        {
            sb.Append("# HELP ").Append(_name).Append(' ').AppendLine(_help);
            sb.Append("# TYPE ").Append(_name).AppendLine(" counter");
            foreach (var (key, val) in _cells)
            {
                sb.Append(_name);
                if (_labels.Length > 0)
                {
                    sb.Append('{');
                    var parts = key.Split(CellSeparator);
                    for (var i = 0; i < _labels.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var v = i < parts.Length ? parts[i] : "";
                        sb.Append(_labels[i]).Append("=\"").Append(EscapeLabel(v)).Append('"');
                    }
                    sb.Append('}');
                }
                sb.Append(' ').Append(val.Value.ToString("0.################", CultureInfo.InvariantCulture)).AppendLine();
            }
        }

        private string CellKey(string[] labelValues)
        {
            if (_labels.Length == 0) return "";
            var sb = new StringBuilder();
            for (var i = 0; i < _labels.Length; i++)
            {
                if (i > 0) sb.Append(CellSeparator);
                sb.Append(i < labelValues.Length ? labelValues[i] : "");
            }
            return sb.ToString();
        }

        private sealed class CounterValue
        {
            private double _v;
            private readonly object _gate = new();
            public double Value { get { lock (_gate) return _v; } }
            public void Add(double by) { lock (_gate) _v += by; }
        }
    }

    /// <summary>Single-valued gauge; thread-safe.</summary>
    public sealed class Gauge : MetricEntry
    {
        private readonly string _name;
        private readonly string _help;
        private long _bits; // bit-pattern of double

        internal Gauge(string name, string help)
        {
            _name = name;
            _help = help;
        }

        public void Set(double value)
            => Interlocked.Exchange(ref _bits, BitConverter.DoubleToInt64Bits(value));

        public void Inc() => Add(1);
        public void Dec() => Add(-1);

        public void Add(double by)
        {
            while (true)
            {
                var current = Interlocked.Read(ref _bits);
                var curD    = BitConverter.Int64BitsToDouble(current);
                var newBits = BitConverter.DoubleToInt64Bits(curD + by);
                if (Interlocked.CompareExchange(ref _bits, newBits, current) == current)
                    return;
            }
        }

        public double Value => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _bits));

        public override void WriteTo(StringBuilder sb)
        {
            sb.Append("# HELP ").Append(_name).Append(' ').AppendLine(_help);
            sb.Append("# TYPE ").Append(_name).AppendLine(" gauge");
            sb.Append(_name).Append(' ').Append(Value.ToString("0.################", CultureInfo.InvariantCulture)).AppendLine();
        }
    }

    private static string EscapeLabel(string v)
    {
        if (v.IndexOfAny(['\\', '"', '\n']) < 0) return v;
        var sb = new StringBuilder(v.Length + 8);
        foreach (var c in v)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                default:   sb.Append(c);      break;
            }
        }
        return sb.ToString();
    }
}
