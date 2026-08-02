// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using NPS.NOP.Orchestration;

namespace NPS.Tests.Nop;

public sealed class NopPortableProfileConformanceTests
{
    [Fact]
    public void SharedOrchestratorTranscriptsPass()
    {
        using var fixture = Load(
            "spec/conformance/nop/orchestrator_transcripts.json");
        var vectors = fixture.RootElement.GetProperty("vectors");
        Assert.Equal(10, vectors.GetArrayLength());

        foreach (var vector in vectors.EnumerateArray())
        {
            var actual = NopPortableProfile.EvaluateOrchestration(
                vector.GetProperty("input"));
            AssertJsonEqual(
                vector.GetProperty("expected"),
                actual,
                vector.GetProperty("id").GetString()!);
        }
    }

    [Fact]
    public void SharedRuntimeSecurityVectorsPass()
    {
        using var fixture = Load(
            "spec/conformance/nop/runtime_security_vectors.json");
        var vectors = fixture.RootElement.GetProperty("vectors");
        Assert.Equal(22, vectors.GetArrayLength());

        foreach (var vector in vectors.EnumerateArray())
        {
            var actual = NopPortableProfile.EvaluateRuntime(
                vector.GetProperty("category").GetString()!,
                vector.GetProperty("input"));
            AssertJsonEqual(
                vector.GetProperty("expected"),
                actual,
                vector.GetProperty("id").GetString()!);
        }
    }

    private static void AssertJsonEqual(
        JsonElement expected,
        JsonElement actual,
        string id)
    {
        var expectedNode = JsonNode.Parse(expected.GetRawText());
        var actualNode = JsonNode.Parse(actual.GetRawText());
        Assert.True(
            JsonNode.DeepEquals(expectedNode, actualNode),
            $"{id}\nexpected: {expectedNode}\nactual:   {actualNode}");
    }

    private static JsonDocument Load(string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(FindRepoFile(relativePath)));

    private static string FindRepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{relativePath}'.");
    }
}
