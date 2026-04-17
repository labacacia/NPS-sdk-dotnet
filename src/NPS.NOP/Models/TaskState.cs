// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP.Models;

/// <summary>
/// Task and sub-task execution states (NPS-5 §5).
/// </summary>
public enum TaskState
{
    Pending,
    Preflight,
    Running,
    WaitingSync,
    Completed,
    Failed,
    Cancelled,
    Skipped,
}
