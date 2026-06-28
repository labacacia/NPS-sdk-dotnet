// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP.Ca;
using NPS.NIP.Frames;

namespace NPS.Tests.Nip;

public sealed class NipFrameValidationTests
{
    [Fact]
    public void RevokeFrame_Validate_RejectsInvalidParentNidShape()
    {
        var missingParent = new RevokeFrame
        {
            TargetNid = "urn:nps:agent:ca.example.com:session-1",
            Reason    = "parent_revoked",
            RevokedAt = "2026-06-01T00:00:00Z",
            SignerNid = "urn:nps:org:ca.example.com",
            Signature = "ed25519:sig",
        };
        var ex1 = Assert.Throws<ArgumentException>(missingParent.Validate);
        Assert.Contains(NipErrorCodes.RevokeInvalid, ex1.Message);

        var strayParent = new RevokeFrame
        {
            TargetNid = "urn:nps:agent:ca.example.com:old",
            Reason    = "key_compromise",
            RevokedAt = "2026-06-01T00:00:00Z",
            ParentNid = "urn:nps:agent:ca.example.com:group-1",
            SignerNid = "urn:nps:org:ca.example.com",
            Signature = "ed25519:sig",
        };
        var ex2 = Assert.Throws<ArgumentException>(strayParent.Validate);
        Assert.Contains(NipErrorCodes.RevokeInvalid, ex2.Message);
    }
}
