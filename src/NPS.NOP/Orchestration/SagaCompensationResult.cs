// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP.Orchestration;

/// <summary>
/// Summary of the Saga compensation run (NPS-5 §3.5).
/// Attached to <see cref="NopTaskResult.Compensation"/> when rollback was attempted.
/// </summary>
public sealed record SagaCompensationResult(
    int Attempted,
    int Succeeded,
    int Failed,
    IReadOnlyList<string> FailedNodeIds);
