// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Conformance;

namespace NPS.Tests.Conformance;

public sealed class AaaSL2RequirementDispositionTests
{
    [Fact]
    public void Every_pre_alpha20_requirement_has_strength_case_and_disposition()
    {
        using var document = LoadDisposition();
        var root = document.RootElement;
        var requirements = root.GetProperty("requirements").EnumerateArray().ToArray();

        Assert.Equal("0.7", root.GetProperty("profile_version").GetString());
        Assert.Equal("0.7", root.GetProperty("suite_version").GetString());
        Assert.Equal(7, requirements.Length);

        var expectedRequirementIds = Enumerable.Range(1, 7).Select(i => $"L2-{i:00}").ToArray();
        var expectedCaseIds = Enumerable.Range(1, 7).Select(i => $"TC-N2-AaaS-{i:00}").ToArray();
        Assert.Equal(expectedRequirementIds, requirements.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(expectedCaseIds, requirements.Select(r => r.GetProperty("case_id").GetString()));

        Assert.All(requirements[..5], requirement =>
        {
            Assert.Equal("MUST", requirement.GetProperty("normative_level").GetString());
            Assert.Equal("current_required", requirement.GetProperty("disposition").GetString());
        });
        Assert.All(requirements[5..], requirement =>
        {
            Assert.Equal("SHOULD", requirement.GetProperty("normative_level").GetString());
            Assert.Equal("current_recommended_exceptionable", requirement.GetProperty("disposition").GetString());
        });

        var catalog = NpsConformanceCatalog.NodeL2
            .Where(c => c.Id.StartsWith("TC-N2-AaaS-", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(expectedCaseIds, catalog.Select(c => c.Id));
        Assert.Equal(expectedRequirementIds, catalog.Select(c => c.Requirement));
        Assert.All(catalog[..5], item => Assert.False(item.Optional));
        Assert.All(catalog[5..], item => Assert.True(item.Optional));
    }

    [Fact]
    public void Should_exception_requires_a_non_empty_reason()
    {
        var results = ValidSingleAnchorResults();
        var asyncCase = results.Single(result => result.Id == "TC-N2-AaaS-06");
        results[results.IndexOf(asyncCase)] = asyncCase with { Result = "na", Message = "  " };

        var invalid = CreateManifest(results);
        var validation = NpsConformanceValidator.Validate(invalid);

        Assert.False(validation.Valid);
        Assert.Contains("requires a non-empty message", validation.Message, StringComparison.Ordinal);

        results[results.FindIndex(result => result.Id == "TC-N2-AaaS-06")] =
            asyncCase with { Result = "na", Message = "Synchronous-only deployment" };
        var valid = CreateManifest(results);
        Assert.True(NpsConformanceValidator.Validate(valid).Valid);
    }

    private static List<NpsConformanceCaseResult> ValidSingleAnchorResults() =>
        NpsConformanceCatalog.NodeL2.Select(item => new NpsConformanceCaseResult
        {
            Id = item.Id,
            Result = item.Id.StartsWith("TC-N2-AaaS-", StringComparison.Ordinal)
                || item.Id.StartsWith("TC-N2-Anchor", StringComparison.Ordinal)
                || item.Id == "TC-N2-HA-09" ? "pass" : "na",
        }).ToList();

    private static NpsConformanceManifest CreateManifest(List<NpsConformanceCaseResult> results) =>
        NpsConformanceManifest.Create(
            NpsConformanceProfiles.NodeL2,
            "single-anchor",
            "0.1.0",
            "urn:nps:node:example.test:anchor-1",
            "reference",
            "1.0.0-alpha.18",
            results);

    private static JsonDocument LoadDisposition() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "conformance",
        "aaas-l2-requirement-disposition.json")));
}
