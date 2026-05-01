// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using DnsClient;

namespace NPS.NDP.Registry;

/// <summary>
/// Production implementation of <see cref="IDnsTxtLookup"/> that performs real DNS TXT
/// queries using the <c>DnsClient</c> NuGet package.
///
/// <para>Multi-part TXT strings (RFC 1035 §3.3.14 allows a single TXT record to have
/// multiple character-string segments) are joined with a single space before being
/// returned, matching how most tools display them.</para>
///
/// <para>Any exception (network failure, NXDOMAIN, timeout) results in an empty list
/// rather than propagating the error to callers.</para>
/// </summary>
public sealed class SystemDnsTxtLookup : IDnsTxtLookup
{
    private readonly LookupClient _client;

    /// <summary>Initialises with default system DNS resolver settings.</summary>
    public SystemDnsTxtLookup() => _client = new LookupClient();

    /// <summary>
    /// Initialises with a custom <see cref="LookupClient"/> (e.g. to point at a
    /// specific DNS server or to set custom timeouts).
    /// </summary>
    public SystemDnsTxtLookup(LookupClient client) => _client = client;

    /// <inheritdoc/>
    public IReadOnlyList<string> Lookup(string hostname)
    {
        try
        {
            var result = _client.Query(hostname, QueryType.TXT);
            if (result.HasError)
                return [];

            return result.Answers
                .TxtRecords()
                .Select(r => string.Join(' ', r.Text))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
