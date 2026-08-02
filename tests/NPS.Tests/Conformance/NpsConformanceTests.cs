// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Conformance;

namespace NPS.Tests.Conformance;

public sealed class NpsConformanceTests
{
    [Fact]
    public void Catalog_ContainsExpectedL1AndL2Cases()
    {
        Assert.Equal(20, NpsConformanceCatalog.NodeL1.Count);
        Assert.Equal(16, NpsConformanceCatalog.NodeL2.Count);
        Assert.Contains(NpsConformanceCatalog.NodeL2, c => c.Id == "TC-N2-Tls-04");
    }

    [Fact]
    public void Validator_AcceptsCompleteL1Manifest()
    {
        var manifest = NpsConformanceManifest.Create(
            NpsConformanceProfiles.NodeL1,
            "iut",
            "0.1",
            "urn:nps:node:test:iut",
            "peer",
            "1.0.0-alpha.17",
            NpsConformanceCatalog.NodeL1.Select(c => new NpsConformanceCaseResult
            {
                Id = c.Id,
                Result = c.Optional ? "na" : "pass",
            }));

        var validation = NpsConformanceValidator.Validate(manifest);

        Assert.True(validation.Valid, validation.Message);
        Assert.Equal(17, manifest.Summary.Pass);
        Assert.Equal(3, manifest.Summary.NotApplicable);
        Assert.Contains("\"profile\": \"NPS-Node-L1\"", manifest.ToJson());
    }

    [Fact]
    public void Validator_RejectsMissingCase()
    {
        var manifest = NpsConformanceManifest.Create(
            NpsConformanceProfiles.NodeL1,
            "iut",
            "0.1",
            "urn:nps:node:test:iut",
            "peer",
            "1.0.0-alpha.17",
            NpsConformanceCatalog.NodeL1.Skip(1).Select(c => new NpsConformanceCaseResult
            {
                Id = c.Id,
                Result = "pass",
            }));

        var validation = NpsConformanceValidator.Validate(manifest);

        Assert.False(validation.Valid);
        Assert.Contains("Missing conformance case results", validation.Message);
    }
}
