// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using NPS.Conformance;

namespace NPS.Tests.Conformance;

public sealed partial class NodeProfileCaseInventoryTests
{
    private static readonly string[] ValidStatuses =
    [
        "executable_iut",
        "component_executable",
        "partial",
        "catalog_only",
        "not_applicable_reference_iut",
    ];

    [Theory]
    [InlineData("node_l1", "NPS-Node-L1.md", 20)]
    [InlineData("node_l2", "NPS-Node-L2.md", 38)]
    public void Inventory_matches_spec_headings_and_runtime_catalog(
        string profileKey,
        string specFile,
        int expectedCount)
    {
        using var inventory = LoadInventory();
        var profile = inventory.RootElement.GetProperty("profiles").GetProperty(profileKey);
        var groups = profile.GetProperty("groups").EnumerateArray().ToArray();
        var inventoryIds = groups
            .SelectMany(group => group.GetProperty("ids").EnumerateArray())
            .Select(id => id.GetString()!)
            .ToArray();

        Assert.Equal(expectedCount, inventoryIds.Length);
        Assert.Equal(expectedCount, inventoryIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expectedCount, profile.GetProperty("spec_case_count").GetInt32());
        Assert.Equal(expectedCount, profile.GetProperty("catalog_case_count").GetInt32());

        var specText = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "conformance",
            "spec",
            specFile));
        var specIds = CaseHeadingRegex().Matches(specText)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var catalog = profileKey == "node_l1"
            ? NpsConformanceCatalog.NodeL1
            : NpsConformanceCatalog.NodeL2;
        var catalogIds = catalog.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(specIds.Order(), inventoryIds.ToHashSet(StringComparer.Ordinal).Order());
        Assert.Equal(specIds.Order(), catalogIds.Order());

        var summary = profile.GetProperty("summary");
        foreach (var status in ValidStatuses)
        {
            var actual = groups
                .Where(group => group.GetProperty("status").GetString() == status)
                .Sum(group => group.GetProperty("ids").GetArrayLength());
            Assert.Equal(summary.GetProperty(status).GetInt32(), actual);
        }
        Assert.All(
            groups,
            group => Assert.Contains(group.GetProperty("status").GetString(), ValidStatuses));
    }

    [Theory]
    [InlineData("NPS-Node-L1.md", "NPS-Node-L1.cn.md")]
    [InlineData("NPS-Node-L2.md", "NPS-Node-L2.cn.md")]
    public void English_and_Chinese_specs_advertise_the_same_cases(
        string englishSpecFile,
        string chineseSpecFile)
    {
        var englishIds = ReadSpecIds(englishSpecFile);
        var chineseIds = ReadSpecIds(chineseSpecFile);

        Assert.Equal(englishIds.Order(), chineseIds.Order());
    }

    [Theory]
    [InlineData("NPS-Node-L1", "0.1", "NPS-NODE-L1-CERTIFIED.md", "NPS-NODE-L1-CERTIFIED.cn.md")]
    [InlineData("NPS-Node-L2", "0.7", "NPS-NODE-L2-CERTIFIED.md", "NPS-NODE-L2-CERTIFIED.cn.md")]
    public void Self_attestation_templates_are_exhaustive_and_bilingual(
        string profile,
        string profileVersion,
        string englishTemplate,
        string chineseTemplate)
    {
        var catalogIds = NpsConformanceCatalog.ForProfile(profile)
            .Select(item => item.Id)
            .Order()
            .ToArray();
        var englishText = ReadSpec(englishTemplate);
        var chineseText = ReadSpec(chineseTemplate);

        Assert.Equal(catalogIds, ReadChecklistIds(englishText));
        Assert.Equal(catalogIds, ReadChecklistIds(chineseText));
        Assert.Contains($"\"profile_version\": \"{profileVersion}\"", englishText);
        Assert.Contains($"\"profile_version\": \"{profileVersion}\"", chineseText);
    }

    private static HashSet<string> ReadSpecIds(string specFile)
    {
        var specText = ReadSpec(specFile);
        return CaseHeadingRegex().Matches(specText)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ReadSpec(string specFile) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "conformance",
        "spec",
        specFile));

    private static string[] ReadChecklistIds(string text) => ChecklistCaseRegex()
        .Matches(text)
        .Select(match => match.Groups[1].Value)
        .Distinct(StringComparer.Ordinal)
        .Order()
        .ToArray();

    private static JsonDocument LoadInventory() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "conformance",
        "node-profile-case-inventory.json")));

    [GeneratedRegex("^####\\s+(TC-[^\\s]+)\\s+", RegexOptions.Multiline)]
    private static partial Regex CaseHeadingRegex();

    [GeneratedRegex("^- \\[ \\] `(TC-[^`]+)`", RegexOptions.Multiline)]
    private static partial Regex ChecklistCaseRegex();
}
