// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using NPS.NDP;
using NPS.NDP.Frames;
using NPS.NDP.Registry;
using NPS.NDP.Validation;

namespace NPS.Tests.Ndp;

public sealed class NdpRegistryConformanceTests
{
    [Fact]
    public void SharedCanonicalizationVectorsPass()
    {
        using var document = Load(
            "spec/conformance/ndp/announce_canonicalization_vectors.json");
        var vectors = document.RootElement.GetProperty("vectors");
        Assert.Equal(3, vectors.GetArrayLength());

        foreach (var vector in vectors.EnumerateArray())
        {
            var input = vector.GetProperty("input");
            var expected = vector.GetProperty("expected");
            var frame = input.GetProperty("frame");

            Assert.Equal(
                expected.GetProperty("canonical_json").GetString(),
                NdpAnnounceCanonicalizer.CanonicalJson(frame));
            Assert.Equal(
                expected.GetProperty("signature_valid").GetBoolean(),
                NdpAnnounceCanonicalizer.Verify(
                    frame,
                    input.GetProperty("public_key").GetString()!,
                    input.GetProperty("signature").GetString()!));

            var wire = JsonNode.Parse(frame.GetRawText())!.AsObject();
            wire["signature"] = input.GetProperty("signature").GetString();
            var model = JsonSerializer.Deserialize<AnnounceFrame>(
                wire.ToJsonString(),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            Assert.NotNull(model);
            var validator = new NdpAnnounceValidator();
            validator.RegisterPublicKey(
                model.Nid,
                input.GetProperty("public_key").GetString()!);
            Assert.Equal(
                expected.GetProperty("signature_valid").GetBoolean(),
                validator.Validate(model).IsValid);
        }
    }

    [Fact]
    public void SharedRegistryConsistencyVectorsPass()
    {
        using var document = Load(
            "spec/conformance/ndp/registry_consistency_vectors.json");
        var vectors = document.RootElement.GetProperty("vectors");
        Assert.Equal(16, vectors.GetArrayLength());

        foreach (var vector in vectors.EnumerateArray())
            AssertRegistryVector(vector);
    }

    private static void AssertRegistryVector(JsonElement vector)
    {
        var input = vector.GetProperty("input");
        var expected = vector.GetProperty("expected");
        var now = DateTimeOffset.Parse(input.GetProperty("now").GetString()!);
        var registry = new NdpRegistryProfile(
            input.GetProperty("profile").GetString()!);
        var decisions = new List<string>();
        var errors = new List<string?>();

        foreach (var announce in input.GetProperty("announces").EnumerateArray())
        {
            var receivedAt = announce.TryGetProperty(
                "received_at",
                out var receivedProperty)
                ? DateTimeOffset.Parse(receivedProperty.GetString()!)
                : now;
            var result = registry.ApplyAnnounce(
                announce.GetProperty("frame"),
                announce.GetProperty("signature_valid").GetBoolean(),
                receivedAt);
            decisions.Add(ToWire(result.Decision));
            errors.Add(result.ErrorCode);
        }

        Assert.Equal(
            expected.GetProperty("decisions")
                .EnumerateArray()
                .Select(item => item.GetString()),
            decisions);
        Assert.Equal(
            expected.GetProperty("errors")
                .EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.Null
                    ? null
                    : item.GetString()),
            errors);
        Assert.Equal(
            expected.GetProperty("live_nids")
                .EnumerateArray()
                .Select(item => item.GetString()),
            registry.LiveNids(now));

        var expectedSequences = expected
            .GetProperty("highest_sequences")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetUInt64(),
                StringComparer.Ordinal);
        Assert.Equal(expectedSequences, registry.HighestSequences);

        if (input.TryGetProperty("cluster_query", out var clusterQuery))
        {
            var selected = registry.ResolveCluster(
                clusterQuery.GetString()!,
                now);
            Assert.Equal(
                OptionalString(expected, "selected_nid"),
                selected.Nid);
            Assert.Equal(
                OptionalUInt64(expected, "selected_epoch"),
                selected.Epoch);
            Assert.Equal(
                OptionalString(expected, "cluster_error"),
                selected.ErrorCode);
        }

        if (input.TryGetProperty("bridge_queries", out var bridgeQueries))
        {
            var expectedResults = expected.GetProperty("bridge_results");
            var index = 0;
            foreach (var query in bridgeQueries.EnumerateArray())
            {
                Assert.Equal(
                    expectedResults[index]
                        .EnumerateArray()
                        .Select(item => item.GetString()),
                    registry.DiscoverBridges(
                        query.GetProperty("direction").GetString()!,
                        query.GetProperty("protocol").GetString()!,
                        now));
                index++;
            }
        }

        if (expected.TryGetProperty("resolve_error", out var resolveError))
        {
            Assert.Equal(NdpErrorCodes.ResolveStale, resolveError.GetString());
            Assert.True(registry.HasStaleEntry(now));
        }
    }

    private static string ToWire(NdpRegistryDecision decision) =>
        decision.ToString().ToLowerInvariant();

    private static string? OptionalString(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;

    private static ulong? OptionalUInt64(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
            ? property.GetUInt64()
            : null;

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
