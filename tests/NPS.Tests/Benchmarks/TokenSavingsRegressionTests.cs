// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Benchmarks.TokenSavings;

namespace NPS.Tests.Benchmarks;

/// <summary>
/// Regression guard for the CGN token-savings benchmark. Phase 1 exit criterion is
/// ≥30% savings across representative Agent↔Node scenarios. If scenario fixtures
/// drift or the counter changes, we want CI to catch it immediately rather than
/// discovering it when the report is re-emitted.
/// </summary>
public class TokenSavingsRegressionTests
{
    private const double Phase1Target = 0.30;

    [Fact]
    public void AggregateSavings_MeetsPhase1Target()
    {
        var results = Scenarios.All.Select(Benchmark.Measure).ToList();
        uint totalRest = 0, totalNwp = 0;
        foreach (var r in results) { totalRest += r.RestNpt; totalNwp += r.NwpTotal; }
        double overall = 1.0 - (double)totalNwp / totalRest;

        Assert.True(overall >= Phase1Target,
            $"Aggregate CGN savings dropped below Phase 1 target: {overall:p1} < {Phase1Target:p1}");
    }

    [Theory]
    [MemberData(nameof(AllScenarios))]
    public void Scenario_MeetsPhase1Target(Scenario scenario)
    {
        var r = Benchmark.Measure(scenario);
        Assert.True(r.SavingsRatio >= Phase1Target,
            $"{scenario.Name}: {r.SavingsRatio:p1} < {Phase1Target:p1}");
    }

    [Fact]
    public void Benchmark_ProducesDeterministicOutput()
    {
        var a = Benchmark.Run();
        var b = Benchmark.Run();
        Assert.Equal(a, b);
    }

    public static IEnumerable<object[]> AllScenarios =>
        Scenarios.All.Select(s => new object[] { s });
}
