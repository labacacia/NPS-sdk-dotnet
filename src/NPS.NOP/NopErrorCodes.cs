// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP;

/// <summary>
/// NOP protocol error codes and their NPS status code mappings (NPS-5 §7).
/// </summary>
public static class NopErrorCodes
{
    public const string TaskNotFound           = "NOP-TASK-NOT-FOUND";
    public const string TaskTimeout            = "NOP-TASK-TIMEOUT";
    public const string TaskDagInvalid         = "NOP-TASK-DAG-INVALID";
    public const string TaskDagCycle           = "NOP-TASK-DAG-CYCLE";
    public const string TaskDagTooLarge        = "NOP-TASK-DAG-TOO-LARGE";
    public const string TaskAlreadyCompleted   = "NOP-TASK-ALREADY-COMPLETED";
    public const string TaskCancelled          = "NOP-TASK-CANCELLED";
    public const string DelegateScopeViolation = "NOP-DELEGATE-SCOPE-VIOLATION";
    public const string DelegateRejected       = "NOP-DELEGATE-REJECTED";
    public const string DelegateChainTooDeep   = "NOP-DELEGATE-CHAIN-TOO-DEEP";
    public const string DelegateTimeout        = "NOP-DELEGATE-TIMEOUT";
    public const string SyncTimeout            = "NOP-SYNC-TIMEOUT";
    public const string SyncDependencyFailed   = "NOP-SYNC-DEPENDENCY-FAILED";
    public const string StreamSeqGap           = "NOP-STREAM-SEQ-GAP";
    public const string StreamNidMismatch      = "NOP-STREAM-NID-MISMATCH";
    public const string ResourceInsufficient   = "NOP-RESOURCE-INSUFFICIENT";
    public const string ConditionEvalError     = "NOP-CONDITION-EVAL-ERROR";
    public const string InputMappingError      = "NOP-INPUT-MAPPING-ERROR";
}
