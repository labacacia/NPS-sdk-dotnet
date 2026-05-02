// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace NPS.Benchmarks.TokenSavings;

/// <summary>
/// CGN (Cognon) counter per <c>spec/token-budget.md §2.2</c>. When the tokenizer
/// cannot be determined, CGN = <c>ceil(UTF-8 bytes / 4)</c>. We use that fallback
/// here so REST and NWP are measured with the same ruler — this is the correct
/// baseline when comparing two protocols without committing to a specific model.
/// </summary>
public static class CognCounter
{
    /// <summary>Count CGN for a single UTF-8 encoded message.</summary>
    public static uint Count(string utf8Text)
    {
        if (string.IsNullOrEmpty(utf8Text)) return 0;
        var bytes = Encoding.UTF8.GetByteCount(utf8Text);
        return (uint)((bytes + 3) / 4);
    }

    /// <summary>Count CGN for a sequence of messages (request + response per call, etc.).</summary>
    public static uint CountAll(IEnumerable<string> messages)
    {
        uint total = 0;
        foreach (var m in messages) total += Count(m);
        return total;
    }
}
