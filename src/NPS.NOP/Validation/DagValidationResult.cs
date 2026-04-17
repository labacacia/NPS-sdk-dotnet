// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP.Validation;

/// <summary>
/// Result of DAG validation.
/// </summary>
public sealed record DagValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Topologically sorted node IDs (populated only when valid).</summary>
    public IReadOnlyList<string>? TopologicalOrder { get; init; }

    public static DagValidationResult Success(IReadOnlyList<string> order) => new()
    {
        IsValid = true,
        TopologicalOrder = order,
    };

    public static DagValidationResult Failure(string errorCode, string message) => new()
    {
        IsValid = false,
        ErrorCode = errorCode,
        ErrorMessage = message,
    };
}
