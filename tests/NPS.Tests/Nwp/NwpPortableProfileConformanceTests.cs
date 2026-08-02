// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.NWP.Bridge;
using NPS.NWP.Portable;

namespace NPS.Tests.Nwp;

public sealed class NwpPortableProfileConformanceTests
{
    [Fact]
    public void PortableNodeServerPolicy_MatchesSharedVectors()
    {
        using var fixture = LoadFixture("spec/conformance/nwp/portable_node_server_vectors.json");

        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var input = vector.GetProperty("input");
            var expected = vector.GetProperty("expected");
            var decision = NwpPortableNodePolicy.Evaluate(new NwpPortableNodeRequest
            {
                Transport = ParseTransport(RequiredString(input, "transport")),
                NodeRole = ParseNodeRole(RequiredString(input, "node_role")),
                Method = OptionalString(input, "method"),
                Path = OptionalString(input, "path"),
                ContentType = OptionalString(input, "content_type"),
                Accept = OptionalString(input, "accept"),
                BodyBytes = OptionalInt64(input, "body_bytes"),
                MaxBodyBytes = OptionalInt64(input, "max_body_bytes", 1024 * 1024),
                FrameKind = OptionalString(input, "frame_kind"),
                BodyValid = OptionalBool(input, "body_valid", true),
                Cancelled = OptionalBool(input, "cancelled"),
                CorrelationId = OptionalString(input, "correlation_id"),
            });

            var id = RequiredString(vector, "id");
            Assert.Equal(RequiredString(expected, "decision"), ToWire(decision.Decision));
            AssertOptional(expected, "http_status", decision.HttpStatus, id);
            AssertOptional(expected, "content_type", decision.ContentType, id);
            AssertOptional(expected, "status", decision.Status, id);
            AssertOptional(expected, "error", decision.Error, id);
            AssertOptional(expected, "allow", decision.Allow, id);
            AssertOptional(expected, "response_frame", decision.ResponseFrame, id);
            AssertOptional(expected, "correlation_id", decision.CorrelationId, id);
            Assert.Equal(RequiredString(expected, "telemetry_outcome"), decision.TelemetryOutcome);

            if (expected.TryGetProperty("legacy_media_type_accepted", out var legacy))
                Assert.Equal(legacy.GetBoolean(), decision.LegacyMediaTypeAccepted);
        }
    }

    [Fact]
    public void BridgeLifecyclePolicy_MatchesSharedVectors()
    {
        using var fixture = LoadFixture("spec/conformance/nwp/bridge_lifecycle_vectors.json");

        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var input = vector.GetProperty("input");
            var expected = vector.GetProperty("expected");
            var decision = BridgeLifecyclePolicy.Evaluate(new BridgeLifecycleRequest
            {
                Protocol = RequiredString(input, "protocol"),
                Endpoint = RequiredString(input, "endpoint"),
                RegisteredProtocols = input.GetProperty("registered_protocols")
                    .EnumerateArray()
                    .Select(item => item.GetString()!)
                    .ToArray(),
                AllowHttp = OptionalBool(input, "allow_http", true),
                RejectPrivate = OptionalBool(input, "reject_private", true),
                AllowedPrefixes = OptionalStrings(input, "allowed_prefixes"),
                TimeoutMs = OptionalInt64(input, "timeout_ms"),
                ElapsedMs = OptionalInt64(input, "elapsed_ms"),
                Cancelled = OptionalBool(input, "cancelled"),
                CorrelationId = OptionalString(input, "correlation_id"),
                TaskMode = OptionalString(input, "task_mode") ?? "sync",
            });

            var id = RequiredString(vector, "id");
            Assert.Equal(RequiredString(expected, "decision"), decision.Decision);
            AssertOptional(expected, "http_status", decision.HttpStatus, id);
            AssertOptional(expected, "status", decision.Status, id);
            AssertOptional(expected, "error", decision.Error, id);
            AssertOptional(expected, "correlation_id", decision.CorrelationId, id);
            AssertOptional(expected, "task_mode", decision.TaskMode, id);
            Assert.Equal(RequiredString(expected, "telemetry_outcome"), decision.TelemetryOutcome);
        }
    }

    private static JsonDocument LoadFixture(string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(FindRepoFile(relativePath)));

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Unable to locate {relativePath} from {AppContext.BaseDirectory}.");
    }

    private static NwpServerTransport ParseTransport(string value) =>
        value switch
        {
            "http" => NwpServerTransport.Http,
            "native" => NwpServerTransport.Native,
            _ => throw new InvalidDataException($"Unknown transport '{value}'."),
        };

    private static NwpPortableNodeRole ParseNodeRole(string value) =>
        value switch
        {
            "memory" => NwpPortableNodeRole.Memory,
            "action" => NwpPortableNodeRole.Action,
            "complex" => NwpPortableNodeRole.Complex,
            _ => throw new InvalidDataException($"Unknown node role '{value}'."),
        };

    private static string ToWire(NwpServerDecisionKind decision) =>
        decision switch
        {
            NwpServerDecisionKind.ServeManifest => "serve_manifest",
            NwpServerDecisionKind.DispatchQuery => "dispatch_query",
            NwpServerDecisionKind.DispatchAction => "dispatch_action",
            NwpServerDecisionKind.Reject => "reject",
            NwpServerDecisionKind.Abort => "abort",
            NwpServerDecisionKind.ErrorFrame => "error_frame",
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null),
        };

    private static string RequiredString(JsonElement element, string name) =>
        element.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"'{name}' must be a string.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) ? property.GetString() : null;

    private static long OptionalInt64(JsonElement element, string name, long defaultValue = 0) =>
        element.TryGetProperty(name, out var property) ? property.GetInt64() : defaultValue;

    private static bool OptionalBool(JsonElement element, string name, bool defaultValue = false) =>
        element.TryGetProperty(name, out var property) ? property.GetBoolean() : defaultValue;

    private static IReadOnlyList<string> OptionalStrings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
            ? property.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : Array.Empty<string>();

    private static void AssertOptional(
        JsonElement expected,
        string name,
        string? actual,
        string vectorId)
    {
        if (expected.TryGetProperty(name, out var property))
            Assert.True(string.Equals(property.GetString(), actual, StringComparison.Ordinal),
                $"{vectorId}: expected {name} '{property.GetString()}', got '{actual}'.");
    }

    private static void AssertOptional(
        JsonElement expected,
        string name,
        int? actual,
        string vectorId)
    {
        if (expected.TryGetProperty(name, out var property))
            Assert.True(property.GetInt32() == actual,
                $"{vectorId}: expected {name} {property.GetInt32()}, got {actual}.");
    }
}
