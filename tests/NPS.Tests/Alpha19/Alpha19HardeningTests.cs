// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using NPS.Core;
using NPS.Core.Alpha19;

namespace NPS.Tests.Alpha19;

public sealed class Alpha19HardeningTests
{
    [Fact]
    public void AllSharedVectorsAreExecutable()
    {
        (string Protocol, string Name)[] suites = [("ncp", "runtime_hardening_vectors.json"), ("nwp", "alpha19_hardening_vectors.json"), ("nip", "renewal_revocation_vectors.json"), ("ndp", "recovery_fence_vectors.json"), ("nop", "replay_retention_vectors.json")];
        var seen = new HashSet<string>();
        foreach (var suite in suites)
            foreach (var vector in JsonNode.Parse(File.ReadAllText(Find($"spec/conformance/{suite.Protocol}/{suite.Name}")))!["vectors"]!.AsArray().OfType<JsonObject>())
            {
                var id = vector["id"]!.GetValue<string>(); Assert.True(seen.Add(id), id);
                var actual = Alpha19Policies.Evaluate(Family(id), vector["input"]!.AsObject());
                Assert.True(JsonNode.DeepEquals(vector["expected"], actual), $"{id}\nactual: {actual}\nexpected: {vector["expected"]}");
            }
        Assert.Equal(47, seen.Count);
    }

    [Fact]
    public void BoundaryBranchesAreNotFixtureConstants()
    {
        var actual = Alpha19Policies.Evaluate(Alpha19PolicyFamily.Ncp, new() { { "client_ping_ms", 0 }, { "server_ping_ms", 2500 } });
        Assert.Equal(2500, actual["effective_interval_ms"]!.GetValue<int>());
    }

    [Fact]
    public void Alpha19ErrorsHaveCanonicalStatusMappings()
    {
        Assert.Equal(10, Alpha19Policies.ErrorToNpsStatus.Count);
        Assert.Equal(NpsStatusCodes.ProtoVersionIncompatible, Alpha19Policies.ErrorToNpsStatus["NCP-EARLY-DATA-REJECTED"]);
        Assert.Equal(NpsStatusCodes.ClientGone, Alpha19Policies.ErrorToNpsStatus["NWP-SUBSCRIBE-LEASE-EXPIRED"]);
        Assert.Equal(NpsStatusCodes.ServerInternal, Alpha19Policies.ErrorToNpsStatus["NDP-STATE-CORRUPT"]);
        Assert.Equal(NpsStatusCodes.LimitResource, Alpha19Policies.ErrorToNpsStatus["NOP-REPLAY-LIMIT"]);
    }

    private static Alpha19PolicyFamily Family(string id) => id switch { _ when id.StartsWith("ncp.") => Alpha19PolicyFamily.Ncp, _ when id.Contains(".metadata.") => Alpha19PolicyFamily.NwpMetadata, _ when id.Contains(".subscription.") => Alpha19PolicyFamily.NwpSubscription, _ when id.Contains(".renewal.") => Alpha19PolicyFamily.NipRenewal, _ when id.Contains(".revocation.") => Alpha19PolicyFamily.NipRevocation, _ when id.Contains(".advisory.") => Alpha19PolicyFamily.NipAdvisory, _ when id.StartsWith("ndp.") => Alpha19PolicyFamily.Ndp, _ => Alpha19PolicyFamily.Nop };
    private static string Find(string relative) { for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) { var candidate = Path.Combine(dir.FullName, relative); if (File.Exists(candidate)) return candidate; } throw new FileNotFoundException(relative); }
}
