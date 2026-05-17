// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace NPS.NIP.Ca.Ra;

/// <summary>
/// Enrollment Tier 1: admits registrations whose <c>identifier</c> matches
/// at least one glob pattern in the operator-configured allowlist
/// (NPS-CR-0005 §3.2). Pattern <c>*</c> matches anything (open CA).
/// </summary>
public sealed class AllowlistPolicy : IEnrollmentPolicy
{
    private readonly IReadOnlyList<Regex> _compiled;

    public AllowlistPolicy(IReadOnlyList<string> patterns)
    {
        _compiled = patterns.Select(GlobToRegex).ToList();
    }

    public Task CheckAsync(
        string entityType, string identifier, string pubKey,
        IReadOnlyList<string> capabilities, string scopeJson, string? metadataJson,
        string? enrollmentToken, CancellationToken ct)
    {
        foreach (var re in _compiled)
        {
            if (re.IsMatch(identifier))
                return Task.CompletedTask;
        }
        throw new NipCaException(
            $"Identifier '{identifier}' does not match any enrollment allowlist pattern.",
            NipErrorCodes.RaNidNotAllowed);
    }

    private static Regex GlobToRegex(string pattern)
    {
        if (pattern == "*")
            return new Regex(".*", RegexOptions.Compiled);
        var escaped = Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.Compiled);
    }
}
