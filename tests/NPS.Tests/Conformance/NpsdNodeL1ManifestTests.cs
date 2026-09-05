// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Conformance;

namespace NPS.Tests.Conformance;

public sealed class NpsdNodeL1ManifestTests
{
    private static readonly IReadOnlyDictionary<string, string> InventoryStatusToManifestStatus =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["executable_iut"] = "verified",
            ["partial"] = "partial",
            ["catalog_only"] = "unexecuted",
            ["not_applicable_reference_iut"] = "not_applicable",
        };

    [Fact]
    public void Manifest_is_exhaustive_runnable_and_matches_the_evidence_inventory()
    {
        using var manifest = LoadJson("implementation", "NPS-NODE-L1-MANIFEST.json");
        using var inventory = LoadJson("node-profile-case-inventory.json");
        var root = manifest.RootElement;

        Assert.Equal("npsd", root.GetProperty("implementation").GetString());
        Assert.Equal("not_claimed", root.GetProperty("certification_claim").GetString());
        Assert.NotEmpty(root.GetProperty("run").GetProperty("commands").EnumerateArray());

        var cases = root.GetProperty("cases").EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        Assert.Equal(
            NpsConformanceCatalog.NodeL1.Select(item => item.Id).Order(),
            cases.Keys.Order());

        var expectedStatuses = inventory.RootElement
            .GetProperty("profiles")
            .GetProperty("node_l1")
            .GetProperty("groups")
            .EnumerateArray()
            .SelectMany(group => group.GetProperty("ids").EnumerateArray().Select(id => new
            {
                Id = id.GetString()!,
                Status = InventoryStatusToManifestStatus[group.GetProperty("status").GetString()!],
            }))
            .ToDictionary(item => item.Id, item => item.Status, StringComparer.Ordinal);

        Assert.All(cases, item =>
            Assert.Equal(expectedStatuses[item.Key], item.Value.GetProperty("status").GetString()));
        Assert.All(
            cases.Where(item => item.Value.GetProperty("status").GetString() == "not_applicable"),
            item => Assert.True(NpsConformanceCatalog.NodeL1.Single(c => c.Id == item.Key).Optional));

        var summary = root.GetProperty("summary");
        foreach (var status in new[] { "verified", "partial", "unexecuted", "not_applicable" })
        {
            Assert.Equal(
                summary.GetProperty(status).GetInt32(),
                cases.Count(item => item.Value.GetProperty("status").GetString() == status));
        }
        Assert.Equal(cases.Count, summary.GetProperty("total_cases").GetInt32());
    }

    private static JsonDocument LoadJson(params string[] pathParts) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(
            [AppContext.BaseDirectory, "conformance", .. pathParts])));
}
