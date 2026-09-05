// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace NPS.Tests.Conformance;

public sealed class AdvertisedImplementationManifestTests
{
    [Fact]
    public void Every_advertised_implementation_has_a_runnable_scope_complete_manifest()
    {
        using var registry = LoadJson("node-profile-implementation-manifests.json");
        var entries = registry.RootElement.GetProperty("implementations").EnumerateArray().ToArray();
        Assert.Equal(new[] { "nps-ingress", "nps-runner", "npsd" }, entries
            .Select(entry => entry.GetProperty("implementation").GetString())
            .Order());

        foreach (var entry in entries)
        {
            var fileName = Path.GetFileName(entry.GetProperty("manifest").GetString()!);
            using var manifest = LoadJson("implementation", fileName);
            var root = manifest.RootElement;

            Assert.Equal(entry.GetProperty("implementation").GetString(), root.GetProperty("implementation").GetString());
            Assert.Equal(entry.GetProperty("profile").GetString(), root.GetProperty("profile").GetString());
            Assert.Equal(entry.GetProperty("profile_version").GetString(), root.GetProperty("profile_version").GetString());
            Assert.Equal(entry.GetProperty("claim").GetString(), root.GetProperty("certification_claim").GetString());

            var run = root.GetProperty("run");
            Assert.True(
                run.TryGetProperty("command", out var command)
                    ? !string.IsNullOrWhiteSpace(command.GetString())
                    : run.GetProperty("commands").GetArrayLength() > 0);

            var cases = root.GetProperty("cases");
            var caseCount = cases.ValueKind == JsonValueKind.Array
                ? cases.GetArrayLength()
                : cases.EnumerateObject().Count();
            Assert.Equal(entry.GetProperty("expected_case_count").GetInt32(), caseCount);
        }
    }

    private static JsonDocument LoadJson(params string[] pathParts) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(
            [AppContext.BaseDirectory, "conformance", .. pathParts])));
}
