// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NDP.Registry;

/// <summary>
/// Abstraction over DNS TXT record resolution.
/// Allows test code to inject a fake implementation without making real DNS calls.
/// </summary>
public interface IDnsTxtLookup
{
    /// <summary>
    /// Returns all TXT record strings for the given hostname.
    /// Multi-part TXT strings are joined with a single space.
    /// Returns an empty list when no records are found or on any error.
    /// </summary>
    /// <param name="hostname">Fully-qualified hostname, e.g. <c>_nps-node.api.example.com</c>.</param>
    IReadOnlyList<string> Lookup(string hostname);
}
