// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NIP.Verification;

/// <summary>
/// Result of a Node-side <see cref="NipIdentVerifier"/> check (NPS-3 §7).
/// </summary>
public sealed record NipIdentVerifyResult
{
    /// <summary>True when all verification steps passed.</summary>
    public bool IsValid { get; init; }

    /// <summary>NIP error code when verification fails (e.g. <c>NIP-CERT-EXPIRED</c>).</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable failure reason.</summary>
    public string? Message { get; init; }

    /// <summary>The verification step number (1–6) that failed, or 0 on success.</summary>
    public int FailedStep { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static NipIdentVerifyResult Ok() =>
        new() { IsValid = true };

    /// <summary>Creates a failed result with the given error code and step number.</summary>
    public static NipIdentVerifyResult Fail(int step, string errorCode, string message) =>
        new() { IsValid = false, FailedStep = step, ErrorCode = errorCode, Message = message };
}
