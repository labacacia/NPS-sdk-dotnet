// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPS.Conformance;

public static class NpsConformanceProfiles
{
    public const string NodeL1 = "NPS-Node-L1";
    public const string NodeL2 = "NPS-Node-L2";
}

public enum NpsConformanceResult
{
    Pass,
    Fail,
    Skip,
    NotApplicable,
}

public sealed record NpsConformanceCase(
    string Id,
    string Profile,
    string Requirement,
    string Title,
    bool Optional = false);

public sealed record NpsConformanceCaseResult
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("result")] public required string Result { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

public sealed record NpsConformanceActor
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("nid")] public string? Nid { get; init; }
}

public sealed record NpsConformanceRun
{
    [JsonPropertyName("date")] public required string Date { get; init; }
    [JsonPropertyName("environment")] public required string Environment { get; init; }
}

public sealed record NpsConformanceSummary
{
    [JsonPropertyName("pass")] public required int Pass { get; init; }
    [JsonPropertyName("fail")] public required int Fail { get; init; }
    [JsonPropertyName("skip")] public required int Skip { get; init; }
    [JsonPropertyName("na")] public required int NotApplicable { get; init; }
}

public sealed record NpsConformanceManifest
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    [JsonPropertyName("profile")] public required string Profile { get; init; }
    [JsonPropertyName("profile_version")] public required string ProfileVersion { get; init; }
    [JsonPropertyName("iut")] public required NpsConformanceActor Iut { get; init; }
    [JsonPropertyName("peer")] public required NpsConformanceActor Peer { get; init; }
    [JsonPropertyName("run")] public required NpsConformanceRun Run { get; init; }
    [JsonPropertyName("cases")] public required IReadOnlyList<NpsConformanceCaseResult> Cases { get; init; }
    [JsonPropertyName("summary")] public required NpsConformanceSummary Summary { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static NpsConformanceManifest FromJson(string json) =>
        JsonSerializer.Deserialize<NpsConformanceManifest>(json, JsonOpts)
        ?? throw new JsonException("Conformance manifest deserialized to null.");

    public static NpsConformanceManifest Create(
        string profile,
        string iutName,
        string iutVersion,
        string iutNid,
        string peerName,
        string peerVersion,
        IEnumerable<NpsConformanceCaseResult> results,
        string environment = "unspecified")
    {
        var cases = results.ToList();
        return new NpsConformanceManifest
        {
            Profile = profile,
            ProfileVersion = profile == NpsConformanceProfiles.NodeL2 ? "0.7" : "0.1",
            Iut = new NpsConformanceActor { Name = iutName, Version = iutVersion, Nid = iutNid },
            Peer = new NpsConformanceActor { Name = peerName, Version = peerVersion },
            Run = new NpsConformanceRun { Date = DateTime.UtcNow.ToString("O"), Environment = environment },
            Cases = cases,
            Summary = new NpsConformanceSummary
            {
                Pass = cases.Count(c => c.Result == "pass"),
                Fail = cases.Count(c => c.Result == "fail"),
                Skip = cases.Count(c => c.Result == "skip"),
                NotApplicable = cases.Count(c => c.Result == "na"),
            },
        };
    }
}

public sealed record NpsConformanceValidation(bool Valid, string Message);

public static class NpsConformanceCatalog
{
    public static IReadOnlyList<NpsConformanceCase> NodeL1 { get; } =
    [
        C("TC-N1-NCP-01", NpsConformanceProfiles.NodeL1, "N1-NCP-01", "Tier-1 JSON frame round-trip"),
        C("TC-N1-NCP-02", NpsConformanceProfiles.NodeL1, "N1-NCP-02", "Hello + Anchor handshake"),
        C("TC-N1-NCP-03", NpsConformanceProfiles.NodeL1, "N1-NCP-03", "Loopback listener default"),
        C("TC-N1-NCP-04", NpsConformanceProfiles.NodeL1, "N1-NCP-04", "Tier-2 negotiation hygiene"),
        C("TC-N1-NIP-01", NpsConformanceProfiles.NodeL1, "N1-NIP-01", "Root keypair generation and permission"),
        C("TC-N1-NIP-02", NpsConformanceProfiles.NodeL1, "N1-NIP-02", "IdentFrame sign and verify"),
        C("TC-N1-NIP-03", NpsConformanceProfiles.NodeL1, "N1-NIP-03", "NID format"),
        C("TC-N1-NIP-04", NpsConformanceProfiles.NodeL1, "N1-NIP-04", "Sub-NID issuance", optional: true),
        C("TC-N1-NDP-01", NpsConformanceProfiles.NodeL1, "N1-NDP-01", "AnnounceFrame carries activation_mode"),
        C("TC-N1-NDP-02", NpsConformanceProfiles.NodeL1, "N1-NDP-02", "AnnounceFrame signature"),
        C("TC-N1-NDP-03", NpsConformanceProfiles.NodeL1, "N1-NDP-03", "ResolveFrame response"),
        C("TC-N1-NDP-04", NpsConformanceProfiles.NodeL1, "N1-NDP-04", "GraphFrame topology snapshot", optional: true),
        C("TC-N1-NWP-01", NpsConformanceProfiles.NodeL1, "N1-NWP-01", "Inbox accepts ActionFrame"),
        C("TC-N1-NWP-02", NpsConformanceProfiles.NodeL1, "N1-NWP-02", "Inbox persists across restart"),
        C("TC-N1-NWP-03", NpsConformanceProfiles.NodeL1, "N1-NWP-03", "NWP pull serves inbox"),
        C("TC-N1-NWP-04", NpsConformanceProfiles.NodeL1, "N1-NWP-04", "100 QPS baseline"),
        C("TC-N1-NWP-05", NpsConformanceProfiles.NodeL1, "N1-NWP-05", "Push path", optional: true),
        C("TC-N1-OBS-01", NpsConformanceProfiles.NodeL1, "N1-OBS-01", "Frame log entry per direction"),
        C("TC-N1-OBS-02", NpsConformanceProfiles.NodeL1, "N1-OBS-02", "Log entry fields"),
        C("TC-N1-OBS-03", NpsConformanceProfiles.NodeL1, "N1-OBS-03", "Log destination flexibility"),
    ];

    public static IReadOnlyList<NpsConformanceCase> NodeL2 { get; } =
    [
        C("TC-N2-AaaS-01", NpsConformanceProfiles.NodeL2, "L2-01", "Internal work uses NOP TaskFrame"),
        C("TC-N2-AaaS-02", NpsConformanceProfiles.NodeL2, "L2-02", "OpenTelemetry TaskFrame context injection"),
        C("TC-N2-AaaS-03", NpsConformanceProfiles.NodeL2, "L2-03", "CGN-Estimate budget and token_est response"),
        C("TC-N2-AaaS-04", NpsConformanceProfiles.NodeL2, "L2-04", "NOP preflight gates worker dispatch"),
        C("TC-N2-AaaS-05", NpsConformanceProfiles.NodeL2, "L2-05", "NOP retry and timeout semantics"),
        C("TC-N2-AaaS-06", NpsConformanceProfiles.NodeL2, "L2-06", "Asynchronous Action lifecycle", optional: true),
        C("TC-N2-AaaS-07", NpsConformanceProfiles.NodeL2, "L2-07", "AlignStream CGN back-pressure", optional: true),
        C("TC-N2-AnchorTopo-01", NpsConformanceProfiles.NodeL2, "L2-08", "Snapshot of a 3-member cluster"),
        C("TC-N2-AnchorTopo-02", NpsConformanceProfiles.NodeL2, "L2-08", "Version monotonicity across joins"),
        C("TC-N2-AnchorTopo-03", NpsConformanceProfiles.NodeL2, "L2-08", "Sub-Anchor member surfaces"),
        C("TC-N2-AnchorStream-01", NpsConformanceProfiles.NodeL2, "L2-08", "member_joined on NDP Announce"),
        C("TC-N2-AnchorStream-02", NpsConformanceProfiles.NodeL2, "L2-08", "member_left on NDP TTL expiry"),
        C("TC-N2-AnchorStream-03", NpsConformanceProfiles.NodeL2, "L2-08", "Resume from topology.since_version"),
        C("TC-N2-AnchorTopo-04", NpsConformanceProfiles.NodeL2, "L2-08", "Unauthorized topology access"),
        C("TC-N2-AnchorTopo-05", NpsConformanceProfiles.NodeL2, "L2-08", "Depth cap exceeded"),
        C("TC-N2-AnchorTopo-06", NpsConformanceProfiles.NodeL2, "L2-08", "Unsupported topology scope"),
        C("TC-N2-AnchorTopo-07", NpsConformanceProfiles.NodeL2, "L2-08", "Unsupported topology filter"),
        C("TC-N2-AnchorTopo-08", NpsConformanceProfiles.NodeL2, "L2-08", "Unsupported reserved topology type"),
        C("TC-N2-AnchorStream-04", NpsConformanceProfiles.NodeL2, "L2-08", "resync_required when version is too old"),
        C("TC-N2-Tls-01", NpsConformanceProfiles.NodeL2, "NPS-RFC-0006", "ALPN nps/1.0 negotiated over TLS 1.3", optional: true),
        C("TC-N2-Tls-02", NpsConformanceProfiles.NodeL2, "NPS-RFC-0006", "Mutual TLS required", optional: true),
        C("TC-N2-Tls-03", NpsConformanceProfiles.NodeL2, "NPS-RFC-0006", "Client cert trust anchor and NID binding", optional: true),
        C("TC-N2-Tls-04", NpsConformanceProfiles.NodeL2, "NPS-RFC-0006", "IdentFrame/certificate NID mismatch", optional: true),
        C("TC-N2-BridgeIn-01", NpsConformanceProfiles.NodeL2, "NPS-CR-0010", "MCP inbound required method set", optional: true),
        C("TC-N2-BridgeIn-02", NpsConformanceProfiles.NodeL2, "NPS-CR-0010", "gRPC inbound round-trip", optional: true),
        C("TC-N2-BridgeIn-03", NpsConformanceProfiles.NodeL2, "NPS-CR-0010", "A2A inbound round-trip", optional: true),
        C("TC-N2-BridgeIn-04", NpsConformanceProfiles.NodeL2, "NPS-CR-0010", "Bare action resolution and ambiguity rejection", optional: true),
        C("TC-N2-BridgeIn-05", NpsConformanceProfiles.NodeL2, "NPS-CR-0010", "Foreign-protocol error mapping", optional: true),
        C("TC-N2-BridgeIn-06", NpsConformanceProfiles.NodeL2, "NPS-CR-0010", "Undeclared protocol or direction refusal", optional: true),
        C("TC-N2-HA-01", NpsConformanceProfiles.NodeL2, "NPS-CR-0009", "cluster_epoch on topology read surfaces", optional: true),
        C("TC-N2-HA-02", NpsConformanceProfiles.NodeL2, "NPS-CR-0009", "Planned anchor_failover wire shape", optional: true),
        C("TC-N2-HA-03", NpsConformanceProfiles.NodeL2, "NPS-CR-0009", "Active-loss failover is terminal", optional: true),
        C("TC-N2-HA-04", NpsConformanceProfiles.NodeL2, "NPS-CR-0009", "Quorum-loss wire shape and read-only mode", optional: true),
        C("TC-N2-HA-05", NpsConformanceProfiles.NodeL2, "NPS-CR-0009", "Standby rejects topology writes", optional: true),
        C("TC-N2-HA-06", NpsConformanceProfiles.NodeL2, "NPS-CR-0009", "Superseded leader is epoch fenced", optional: true),
        C("TC-N2-HA-07", NpsConformanceProfiles.NodeL2, "NPS-CR-0009", "Registry resolves highest cluster_epoch", optional: true),
        C("TC-N2-HA-08", NpsConformanceProfiles.NodeL2, "NPS-CR-0009", "Equal-epoch split-brain rejection", optional: true),
        C("TC-N2-HA-09", NpsConformanceProfiles.NodeL2, "NPS-CR-0009", "Single-Anchor epoch-one compatibility", optional: true),
    ];

    public static IReadOnlyList<NpsConformanceCase> ForProfile(string profile) => profile switch
    {
        NpsConformanceProfiles.NodeL1 => NodeL1,
        NpsConformanceProfiles.NodeL2 => NodeL2,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown NPS conformance profile."),
    };

    private static NpsConformanceCase C(string id, string profile, string requirement, string title, bool optional = false) =>
        new(id, profile, requirement, title, optional);
}

public static class NpsConformanceValidator
{
    private static readonly HashSet<string> ValidResults = ["pass", "fail", "skip", "na"];
    private static readonly HashSet<string> ReasonedShouldCases =
        ["TC-N2-AaaS-06", "TC-N2-AaaS-07"];
    private static readonly string[][] L2AllOrNothingFamilies =
    [
        ["TC-N2-Tls-01", "TC-N2-Tls-02", "TC-N2-Tls-03", "TC-N2-Tls-04"],
        ["TC-N2-BridgeIn-01", "TC-N2-BridgeIn-02", "TC-N2-BridgeIn-03", "TC-N2-BridgeIn-04", "TC-N2-BridgeIn-05", "TC-N2-BridgeIn-06"],
        ["TC-N2-HA-01", "TC-N2-HA-02", "TC-N2-HA-03", "TC-N2-HA-04", "TC-N2-HA-05", "TC-N2-HA-06"],
        ["TC-N2-HA-07", "TC-N2-HA-08"],
    ];

    public static NpsConformanceValidation Validate(NpsConformanceManifest manifest)
    {
        var catalog = NpsConformanceCatalog.ForProfile(manifest.Profile);
        var known = catalog.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var result in manifest.Cases)
        {
            if (!known.ContainsKey(result.Id))
                return new(false, $"Unknown conformance case id '{result.Id}'.");
            if (!seen.Add(result.Id))
                return new(false, $"Duplicate conformance case id '{result.Id}'.");
            if (!ValidResults.Contains(result.Result))
                return new(false, $"Case '{result.Id}' has invalid result '{result.Result}'.");
            if (result.Result == "na" && !known[result.Id].Optional)
                return new(false, $"Case '{result.Id}' is required and cannot be marked na.");
            if (result.Result == "na" && ReasonedShouldCases.Contains(result.Id)
                && string.IsNullOrWhiteSpace(result.Message))
                return new(false, $"Case '{result.Id}' requires a non-empty message for a SHOULD exception.");
        }

        var missing = catalog.Where(c => !seen.Contains(c.Id)).Select(c => c.Id).ToList();
        if (missing.Count > 0)
            return new(false, $"Missing conformance case results: {string.Join(", ", missing)}.");

        if (manifest.Cases.Any(c => c.Result is "fail" or "skip"))
            return new(false, "Conformance manifest contains fail or skip results.");

        var expectedVersion = manifest.Profile == NpsConformanceProfiles.NodeL2 ? "0.7" : "0.1";
        if (manifest.ProfileVersion != expectedVersion)
            return new(false, $"Profile '{manifest.Profile}' requires manifest version '{expectedVersion}'.");

        var expectedSummary = new NpsConformanceSummary
        {
            Pass = manifest.Cases.Count(c => c.Result == "pass"),
            Fail = manifest.Cases.Count(c => c.Result == "fail"),
            Skip = manifest.Cases.Count(c => c.Result == "skip"),
            NotApplicable = manifest.Cases.Count(c => c.Result == "na"),
        };
        if (manifest.Summary != expectedSummary)
            return new(false, "Conformance manifest summary does not match case results.");

        if (manifest.Profile == NpsConformanceProfiles.NodeL2)
        {
            var results = manifest.Cases.ToDictionary(c => c.Id, c => c.Result, StringComparer.Ordinal);
            foreach (var family in L2AllOrNothingFamilies)
            {
                var familyResults = family.Select(id => results[id]).Distinct(StringComparer.Ordinal).ToArray();
                if (familyResults.Length != 1 || familyResults[0] is not ("pass" or "na"))
                    return new(false, $"L2 case family '{family[0]}' must be all pass or all na.");
            }

            var anchorHaIsNa = results["TC-N2-HA-01"] == "na";
            var singleAnchorIsNa = results["TC-N2-HA-09"] == "na";
            if (anchorHaIsNa == singleAnchorIsNa)
                return new(false, "L2 multi-Anchor HA and single-Anchor compatibility cases must have opposite applicability.");
        }

        return new(true, "Conformance manifest is valid.");
    }
}
