// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP.Frames;
using NPS.NIP.Verification;

namespace NPS.Tests.Nip;

public sealed class TrustFrameValidatorTests
{
    private static TrustFrame Frame(string? expiresAt = null) => new()
    {
        GrantorNid = "urn:nps:org-a:ca",
        GranteeCa  = "urn:nps:org-b:ca",
        TrustScope = ["nwp:query"],
        Nodes      = ["nwp://api.example.com/*"],
        IssuedAt   = "2026-06-25T00:00:00Z",
        ExpiresAt  = expiresAt ?? "2030-01-01T00:00:00Z",
        Serial     = "00000000000A3F9C",
        SignerNid  = "urn:nps:org-a:ca",
        Signature  = "ed25519:test",
    };

    private static TrustFrameValidationContext Context() => new()
    {
        TrustedGrantors      = new HashSet<string>(StringComparer.Ordinal) { "urn:nps:org-a:ca" },
        ExpectedGranteeCa    = "urn:nps:org-b:ca",
        RequiredCapabilities = ["nwp:query"],
        TargetNodePath       = "nwp://api.example.com/products",
        AsOf                 = new DateTime(2026, 06, 25, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Validate_OpenPinnedTrustFrame_Passes()
    {
        var result = TrustFrameValidator.Validate(Frame(), Context());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_UntrustedGrantor_Fails()
    {
        var context = Context() with
        {
            TrustedGrantors = new HashSet<string>(StringComparer.Ordinal) { "urn:nps:other:ca" },
        };

        var result = TrustFrameValidator.Validate(Frame(), context);

        Assert.False(result.IsValid);
        Assert.Equal("NIP-CERT-UNTRUSTED-ISSUER", result.ErrorCode);
    }

    [Fact]
    public void Validate_ExpiredFrame_Fails()
    {
        var result = TrustFrameValidator.Validate(Frame("2020-01-01T00:00:00Z"), Context());

        Assert.False(result.IsValid);
        Assert.Equal("NIP-TRUST-FRAME-EXPIRED", result.ErrorCode);
    }
}
