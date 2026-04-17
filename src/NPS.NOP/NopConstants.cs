// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NOP;

/// <summary>
/// Protocol-level limits defined by NPS-5 §8.2.
/// </summary>
public static class NopConstants
{
    /// <summary>Maximum number of nodes in a single DAG.</summary>
    public const int MaxDagNodes = 32;

    /// <summary>Maximum delegation chain depth (Orchestrator → Worker → Sub-Worker).</summary>
    public const int MaxDelegateChainDepth = 3;

    /// <summary>Maximum length of a CEL condition expression in characters.</summary>
    public const int MaxConditionLength = 512;

    /// <summary>Maximum JSONPath nesting depth in input_mapping values.</summary>
    public const int MaxInputMappingDepth = 8;

    /// <summary>Default task timeout in milliseconds.</summary>
    public const uint DefaultTimeoutMs = 30000;

    /// <summary>Maximum task timeout in milliseconds (1 hour).</summary>
    public const uint MaxTimeoutMs = 3600000;

    /// <summary>Default AnchorFrame TTL in seconds.</summary>
    public const uint DefaultAnchorTtl = 3600;

    /// <summary>
    /// Maximum number of callback POST attempts with exponential backoff (NPS-5 §8.4).
    /// Attempts use delays: 0 s, 1 s, 2 s (first attempt is immediate).
    /// </summary>
    public const int CallbackMaxRetries = 3;
}
