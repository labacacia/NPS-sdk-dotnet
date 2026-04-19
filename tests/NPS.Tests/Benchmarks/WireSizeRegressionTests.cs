// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Benchmarks.WireSize;

namespace NPS.Tests.Benchmarks;

/// <summary>
/// Regression guard for the Tier-2 MsgPack wire-size benchmark.
/// <para>
/// The Phase 2 exit criterion (<c>spec/NPS-Roadmap.md</c>) is
/// <b>Tier-2 body ≤ 50 % of Tier-1 JSON</b> — i.e. aggregate reduction ≥ 50 %.
/// Steady-state data frames (capsules, actions) MUST each clear the bar on
/// their own; the schema-heavy AnchorFrame is measured but not gated, since
/// its payload is mostly string content and is paid once per session.
/// </para>
/// </summary>
public class WireSizeRegressionTests
{
    /// <summary>Maximum Tier-2 / Tier-1 ratio allowed for steady-state frames.</summary>
    private const double SteadyStateMaxRatio = Benchmark.Phase2MaxRatio; // 0.50

    [Fact]
    public void AggregateRatio_MeetsPhase2Target()
    {
        var results = Scenarios.All.Select(Benchmark.Measure).ToList();
        long totalJson = results.Sum(r => (long)r.JsonBytes);
        long totalMp   = results.Sum(r => (long)r.MsgPackBytes);
        double ratio   = (double)totalMp / totalJson;

        Assert.True(ratio <= SteadyStateMaxRatio,
            $"Aggregate Tier-2/Tier-1 ratio regressed above Phase 2 target: " +
            $"{ratio:p1} > {SteadyStateMaxRatio:p1}");
    }

    [Theory]
    [MemberData(nameof(SteadyStateScenarios))]
    public void SteadyStateScenario_MeetsPhase2Target(Scenario scenario)
    {
        var r = Benchmark.Measure(scenario);
        Assert.True(r.Ratio <= SteadyStateMaxRatio,
            $"{scenario.Name}: Tier-2/Tier-1 ratio {r.Ratio:p1} > {SteadyStateMaxRatio:p1}");
    }

    [Fact]
    public void AnchorFrame_TierRatio_IsLockedAgainstRegression()
    {
        // AnchorFrame is schema-text-heavy and one-shot — MsgPack saves mostly on
        // structural overhead, not on string content. Lock the current ratio to
        // catch regressions without claiming the 50 % bar applies here.
        var anchor = Scenarios.All.First(s => s.Name.StartsWith("S1"));
        var r = Benchmark.Measure(anchor);

        Assert.True(r.Ratio <= 0.80,
            $"AnchorFrame Tier-2/Tier-1 ratio regressed: {r.Ratio:p1} > 80.0 %");
    }

    [Fact]
    public void Benchmark_ProducesDeterministicOutput()
    {
        var a = Benchmark.Run();
        var b = Benchmark.Run();
        Assert.Equal(a, b);
    }

    /// <summary>All scenarios except the AnchorFrame-only fixture (S1).</summary>
    public static IEnumerable<object[]> SteadyStateScenarios =>
        Scenarios.All
            .Where(s => !s.Name.StartsWith("S1"))
            .Select(s => new object[] { s });
}
