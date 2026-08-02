// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP.Ca;

namespace NPS.NIP.Verification;

/// <summary>Receiver policy for NIP v0.13 live revocation.</summary>
public enum NipRevocationMode
{
    IfConfigured,
    Required,
}

/// <summary>Portable revocation source names in normative consultation order.</summary>
public enum NipRevocationSource
{
    LocalCrl,
    Callback,
    CaStore,
    Ocsp,
}

/// <summary>Normalized result returned by a configured revocation source.</summary>
public enum NipRevocationOutcome
{
    Good,
    Revoked,
    Unavailable,
}

/// <summary>
/// Incremental evaluator used by the verifier and shared conformance runners.
/// </summary>
public sealed class NipRevocationEvaluation
{
    private readonly NipRevocationMode _mode;
    private readonly bool _ocspFailOpen;
    private readonly List<NipRevocationSource> _consulted = [];

    public NipRevocationEvaluation(
        NipRevocationMode mode,
        bool ocspFailOpen)
    {
        _mode = mode;
        _ocspFailOpen = ocspFailOpen;
    }

    public IReadOnlyList<NipRevocationSource> ConsultedSources => _consulted;

    public NipIdentVerifyResult? Observe(
        NipRevocationSource source,
        NipRevocationOutcome outcome)
    {
        _consulted.Add(source);

        return outcome switch
        {
            NipRevocationOutcome.Good => null,
            NipRevocationOutcome.Revoked => NipIdentVerifyResult.Fail(
                4,
                NipErrorCodes.CertRevoked,
                $"Revocation source {source} reports the certificate revoked."),
            NipRevocationOutcome.Unavailable
                when source == NipRevocationSource.Ocsp && _ocspFailOpen => null,
            NipRevocationOutcome.Unavailable => NipIdentVerifyResult.Fail(
                4,
                NipErrorCodes.OcspUnavailable,
                $"Revocation source {source} is unavailable."),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
    }

    public NipIdentVerifyResult Complete()
    {
        if (_mode == NipRevocationMode.Required && _consulted.Count == 0)
        {
            return NipIdentVerifyResult.Fail(
                4,
                NipErrorCodes.OcspUnavailable,
                "Revocation mode is required, but no revocation source is configured.");
        }

        return NipIdentVerifyResult.Ok();
    }
}
