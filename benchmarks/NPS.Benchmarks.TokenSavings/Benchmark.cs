// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace NPS.Benchmarks.TokenSavings;

/// <summary>
/// Runs every scenario in <see cref="Scenarios.All"/> through <see cref="CognCounter"/>,
/// then formats the results as a Markdown report. Deliberately free of measurement
/// noise — every byte is deterministic, so re-runs produce byte-identical output.
/// </summary>
public static class Benchmark
{
    /// <summary>Run all scenarios and return the full Markdown report.</summary>
    public static string Run()
    {
        var rows = Scenarios.All.Select(Measure).ToList();
        return Format(rows);
    }

    /// <summary>Measure a single scenario and return its aggregated result row.</summary>
    public static Result Measure(Scenario s)
    {
        uint restNpt = CognCounter.CountAll(
            s.Rest.SelectMany(pair => new[] { pair.Request, pair.Response }));

        uint nwpOneShotNpt = CognCounter.CountAll(s.NwpOneShot);
        uint nwpSteadyNpt  = CognCounter.CountAll(
            s.Nwp.SelectMany(pair => new[] { pair.Request, pair.Response }));
        uint nwpTotalNpt   = nwpOneShotNpt + nwpSteadyNpt;

        double savings = restNpt == 0 ? 0 : 1.0 - (double)nwpTotalNpt / restNpt;

        return new Result(
            Scenario:     s,
            RestNpt:      restNpt,
            NwpOneShot:   nwpOneShotNpt,
            NwpSteady:    nwpSteadyNpt,
            NwpTotal:     nwpTotalNpt,
            SavingsRatio: savings);
    }

    private static string Format(IReadOnlyList<Result> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cognon Savings Benchmark");
        sb.AppendLine();
        sb.AppendLine("Measures Cognon (CGN) consumption for representative Agent↔Node");
        sb.AppendLine("interactions, comparing an idiomatic REST implementation against the");
        sb.AppendLine("equivalent NWP traffic.");
        sb.AppendLine();
        sb.AppendLine("**Counter**: `ceil(UTF-8 bytes / 4)` per `spec/token-budget.md §2.2` —");
        sb.AppendLine("the tokenizer-agnostic baseline. Both protocols are measured with the");
        sb.AppendLine("same ruler, so the ratio is an apples-to-apples comparison.");
        sb.AppendLine();
        sb.AppendLine("**Target** (Phase 1 exit criterion): ≥ 30% CGN savings across scenarios.");
        sb.AppendLine();
        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Calls | REST CGN | NWP Anchor (once) | NWP steady-state | NWP total | Savings |");
        sb.AppendLine("| --- | ---:| ---:| ---:| ---:| ---:| ---:|");

        uint totalRest = 0, totalNwp = 0;
        foreach (var r in rows)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} | {1} | {2:n0} | {3:n0} | {4:n0} | {5:n0} | **{6:p1}** |",
                r.Scenario.Name, r.Scenario.Calls, r.RestNpt,
                r.NwpOneShot, r.NwpSteady, r.NwpTotal, r.SavingsRatio));
            totalRest += r.RestNpt;
            totalNwp  += r.NwpTotal;
        }

        double overall = totalRest == 0 ? 0 : 1.0 - (double)totalNwp / totalRest;
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "| **Aggregate** | — | **{0:n0}** | — | — | **{1:n0}** | **{2:p1}** |",
            totalRest, totalNwp, overall));

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
                "- Calls: {0}", r.Scenario.Calls));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "- REST CGN: {0:n0}", r.RestNpt));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "- NWP CGN (one-shot {0} + steady {1}): {2:n0}",
                r.NwpOneShot, r.NwpSteady, r.NwpTotal));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "- Savings: **{0:p1}**", r.SavingsRatio));
            sb.AppendLine();
        }

        sb.AppendLine("## Reproducibility");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine("dotnet run --project impl/dotnet/benchmarks/NPS.Benchmarks.TokenSavings -- --emit");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Fixtures live in `Scenarios.cs` and are intentionally deterministic so");
        sb.AppendLine("re-runs produce byte-identical output.");

        return sb.ToString();
    }

    /// <summary>Aggregated per-scenario measurement.</summary>
    public sealed record Result(
        Scenario Scenario,
        uint RestNpt,
        uint NwpOneShot,
        uint NwpSteady,
        uint NwpTotal,
        double SavingsRatio);
}
