// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP.Ca;
using NPS.NIP.Frames;

namespace NPS.NIP.Verification;

/// <summary>
/// Basic open TrustFrame validator for self-hosted deployments that pin trusted
/// grantor anchors explicitly. It checks frame shape, expiry, grantor/grantee
/// membership, required capability scope, and target node scope.
/// </summary>
public static class TrustFrameValidator
{
    public static NipIdentVerifyResult Validate(
        TrustFrame frame,
        TrustFrameValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(frame.GrantorNid)
            || string.IsNullOrWhiteSpace(frame.GranteeCa)
            || string.IsNullOrWhiteSpace(frame.Signature)
            || frame.TrustScope.Count == 0
            || frame.Nodes.Count == 0)
        {
            return NipIdentVerifyResult.Fail(3,
                NipErrorCodes.TrustInvalid,
                "TrustFrame is missing grantor, grantee, signature, trust_scope, or nodes.");
        }

        if (!DateTime.TryParse(frame.ExpiresAt, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt))
        {
            return NipIdentVerifyResult.Fail(3,
                NipErrorCodes.TrustInvalid,
                $"TrustFrame expires_at is not a valid timestamp: {frame.ExpiresAt}.");
        }

        var now = context.AsOf ?? DateTime.UtcNow;
        if (expiresAt <= now)
        {
            return NipIdentVerifyResult.Fail(3,
                "NIP-TRUST-FRAME-EXPIRED",
                $"TrustFrame expired at {frame.ExpiresAt}.");
        }

        if (!context.TrustedGrantors.Contains(frame.GrantorNid))
        {
            return NipIdentVerifyResult.Fail(3,
                NipErrorCodes.CertUntrusted,
                $"TrustFrame grantor '{frame.GrantorNid}' is not a trusted grantor.");
        }

        if (!string.Equals(frame.GranteeCa, context.ExpectedGranteeCa, StringComparison.Ordinal))
        {
            return NipIdentVerifyResult.Fail(3,
                NipErrorCodes.TrustInvalid,
                $"TrustFrame grantee '{frame.GranteeCa}' does not match expected CA '{context.ExpectedGranteeCa}'.");
        }

        if (context.RequiredCapabilities is { Count: > 0 })
        {
            var granted = frame.TrustScope.ToHashSet(StringComparer.Ordinal);
            var missing = context.RequiredCapabilities
                .Where(c => !granted.Contains(c))
                .ToArray();
            if (missing.Length > 0)
            {
                return NipIdentVerifyResult.Fail(5,
                    "NIP-TRUST-FRAME-SCOPE-EXCEEDS-GRANTOR",
                    $"TrustFrame is missing required capabilities: {string.Join(", ", missing)}.");
            }
        }

        if (context.TargetNodePath is not null)
        {
            var covered = frame.Nodes.Any(pattern => NipIdentVerifier.NwpPathMatches(pattern, context.TargetNodePath));
            if (!covered)
            {
                return NipIdentVerifyResult.Fail(6,
                    NipErrorCodes.CertScope,
                    $"Target path '{context.TargetNodePath}' is not covered by the TrustFrame node scope.");
            }
        }

        return NipIdentVerifyResult.Ok();
    }
}

/// <summary>Inputs for <see cref="TrustFrameValidator"/>.</summary>
public sealed record TrustFrameValidationContext
{
    /// <summary>Grantor CA NIDs that this node trusts as anchors.</summary>
    public required IReadOnlySet<string> TrustedGrantors { get; init; }

    /// <summary>The CA NID expected to be authorized by the TrustFrame.</summary>
    public required string ExpectedGranteeCa { get; init; }

    /// <summary>Capabilities required for the current request.</summary>
    public IReadOnlyList<string>? RequiredCapabilities { get; init; }

    /// <summary>Target NWP path required for the current request.</summary>
    public string? TargetNodePath { get; init; }

    /// <summary>Clock override for tests.</summary>
    public DateTime? AsOf { get; init; }
}
