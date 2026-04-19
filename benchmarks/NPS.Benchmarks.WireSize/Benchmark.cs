// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using NPS.Core.Codecs;

namespace NPS.Benchmarks.WireSize;

/// <summary>
/// Runs every scenario in <see cref="Scenarios.All"/> through both codecs and
/// formats the results as a Markdown report. Deterministic — re-runs produce
/// byte-identical output.
/// </summary>
public static class Benchmark
{
    /// <summary>The Phase 2 exit criterion from <c>spec/NPS-Roadmap.md</c>:
    /// Tier-2 MsgPack payload MUST be ≤ 50 % of Tier-1 JSON (i.e. ≥ 50 % smaller).</summary>
    public const double Phase2MaxRatio = 0.50;

    /// <summary>Run all scenarios and return the full Markdown report.</summary>
    public static string Run()
    {
        var rows = Scenarios.All.Select(Measure).ToList();
        return Format(rows);
    }

    /// <summary>Measure a single scenario and return its aggregated result row.</summary>
    public static Result Measure(Scenario s)
    {
        var json    = new Tier1JsonCodec().Encode(s.Frame);
        var msgpack = new Tier2MsgPackCodec().Encode(s.Frame);

        int jsonBytes = json.Length;
        int mpBytes   = msgpack.Length;
        double ratio  = jsonBytes == 0 ? 0 : (double)mpBytes / jsonBytes;

        return new Result(s, jsonBytes, mpBytes, ratio);
    }

    private static string Format(IReadOnlyList<Result> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# NPS Tier-2 MsgPack Wire-Size Benchmark");
        sb.AppendLine();
        sb.AppendLine("Compares Tier-1 JSON (`Tier1JsonCodec`) against Tier-2 MsgPack");
        sb.AppendLine("(`Tier2MsgPackCodec`) payload sizes for representative NPS frames.");
        sb.AppendLine("Both codecs serialise the **same** `IFrame` instance, so the ratio is");
        sb.AppendLine("an apples-to-apples payload-only comparison (no header, no compression).");
        sb.AppendLine();
        sb.AppendLine("**Target** (Phase 2 exit criterion, `spec/NPS-Roadmap.md`): Tier-2 body ≤");
        sb.AppendLine("50 % of Tier-1 JSON — i.e. **≥ 50 % reduction**.");
        sb.AppendLine();
        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Tier-1 JSON (bytes) | Tier-2 MsgPack (bytes) | Ratio (Tier-2 / Tier-1) | Reduction |");
        sb.AppendLine("| --- | ---:| ---:| ---:| ---:|");

        long totalJson = 0, totalMp = 0;
        foreach (var r in rows)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} | {1:n0} | {2:n0} | {3:p1} | **{4:p1}** |",
                r.Scenario.Name, r.JsonBytes, r.MsgPackBytes, r.Ratio, 1.0 - r.Ratio));
            totalJson += r.JsonBytes;
            totalMp   += r.MsgPackBytes;
        }

        double overallRatio = totalJson == 0 ? 0 : (double)totalMp / totalJson;
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "| **Aggregate** | **{0:n0}** | **{1:n0}** | **{2:p1}** | **{3:p1}** |",
            totalJson, totalMp, overallRatio, 1.0 - overallRatio));

        sb.AppendLine();
        sb.AppendLine("## Scenarios");
        sb.AppendLine();
        foreach (var r in rows)
        {
            sb.AppendLine($"### {r.Scenario.Name}");
            sb.AppendLine();
            sb.AppendLine(r.Scenario.Description);
            sb.AppendLine();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "- Tier-1 JSON:    {0:n0} bytes", r.JsonBytes));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "- Tier-2 MsgPack: {0:n0} bytes", r.MsgPackBytes));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "- Ratio:          {0:p1}   (**{1:p1} reduction**)",
                r.Ratio, 1.0 - r.Ratio));
            sb.AppendLine();
        }

        sb.AppendLine("## Reproducibility");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine("dotnet run --project impl/dotnet/benchmarks/NPS.Benchmarks.WireSize -- --emit");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Fixtures live in `Scenarios.cs` and are intentionally deterministic so");
        sb.AppendLine("re-runs produce byte-identical output. Regression tests in");
        sb.AppendLine("`NPS.Tests/Benchmarks/WireSizeRegressionTests.cs` lock the aggregate and");
        sb.AppendLine("per-scenario reductions against the Phase 2 target.");

        return sb.ToString();
    }

    /// <summary>Aggregated per-scenario measurement.</summary>
    public sealed record Result(
        Scenario Scenario,
        int      JsonBytes,
        int      MsgPackBytes,
        double   Ratio);
}
