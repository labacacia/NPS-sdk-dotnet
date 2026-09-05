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
        Assert.Equal(38, NpsConformanceCatalog.NodeL2.Count);
        Assert.Contains(NpsConformanceCatalog.NodeL2, c => c.Id == "TC-N2-AaaS-07");
        Assert.Contains(NpsConformanceCatalog.NodeL2, c => c.Id == "TC-N2-Tls-04");
        Assert.Contains(NpsConformanceCatalog.NodeL2, c => c.Id == "TC-N2-BridgeIn-06");
        Assert.Contains(NpsConformanceCatalog.NodeL2, c => c.Id == "TC-N2-HA-09");
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
            "1.0.0-alpha.18",
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
            "1.0.0-alpha.18",
            NpsConformanceCatalog.NodeL1.Skip(1).Select(c => new NpsConformanceCaseResult
            {
                Id = c.Id,
                Result = "pass",
            }));

        var validation = NpsConformanceValidator.Validate(manifest);

        Assert.False(validation.Valid);
        Assert.Contains("Missing conformance case results", validation.Message);
    }

    [Fact]
    public void Validator_AcceptsSingleAnchorL2ManifestWithWholeOptionalFamiliesNa()
    {
        var manifest = NpsConformanceManifest.Create(
            NpsConformanceProfiles.NodeL2,
            "single-anchor",
            "0.1",
            "urn:nps:node:test:anchor",
            "peer",
            "1.0.0-alpha.18",
            NpsConformanceCatalog.NodeL2.Select(c => new NpsConformanceCaseResult
            {
                Id = c.Id,
                Result = c.Id.StartsWith("TC-N2-AaaS-", StringComparison.Ordinal)
                    || c.Id.StartsWith("TC-N2-Anchor", StringComparison.Ordinal)
                    || c.Id == "TC-N2-HA-09" ? "pass" : "na",
            }));

        var validation = NpsConformanceValidator.Validate(manifest);

        Assert.True(validation.Valid, validation.Message);
        Assert.Equal("0.7", manifest.ProfileVersion);
    }

    [Fact]
    public void Validator_RejectsPartialL2FamilyNa()
    {
        var manifest = NpsConformanceManifest.Create(
            NpsConformanceProfiles.NodeL2,
            "single-anchor",
            "0.1",
            "urn:nps:node:test:anchor",
            "peer",
            "1.0.0-alpha.18",
            NpsConformanceCatalog.NodeL2.Select(c => new NpsConformanceCaseResult
            {
                Id = c.Id,
                Result = c.Id.StartsWith("TC-N2-AaaS-", StringComparison.Ordinal)
                    || c.Id.StartsWith("TC-N2-Anchor", StringComparison.Ordinal)
                    || c.Id == "TC-N2-Tls-01"
                    || c.Id == "TC-N2-HA-09" ? "pass" : "na",
            }));

        var validation = NpsConformanceValidator.Validate(manifest);

        Assert.False(validation.Valid);
        Assert.Contains("must be all pass or all na", validation.Message);
    }

    [Fact]
    public void Validator_RejectsMissingSingleAndMultiAnchorHaApplicability()
    {
        var manifest = NpsConformanceManifest.Create(
            NpsConformanceProfiles.NodeL2,
            "invalid-anchor",
            "0.1",
            "urn:nps:node:test:anchor",
            "peer",
            "1.0.0-alpha.18",
            NpsConformanceCatalog.NodeL2.Select(c => new NpsConformanceCaseResult
            {
                Id = c.Id,
                Result = c.Id.StartsWith("TC-N2-AaaS-", StringComparison.Ordinal)
                    || c.Id.StartsWith("TC-N2-Anchor", StringComparison.Ordinal) ? "pass" : "na",
            }));

        var validation = NpsConformanceValidator.Validate(manifest);

        Assert.False(validation.Valid);
        Assert.Contains("opposite applicability", validation.Message);
    }
}
