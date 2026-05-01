// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NDP.Frames;

namespace NPS.NDP.Registry;

/// <summary>
/// NDP node registry — stores <see cref="AnnounceFrame"/> entries and resolves
/// <c>nwp://</c> addresses to physical endpoints.
/// </summary>
public interface INdpRegistry
{
    /// <summary>
    /// Registers or refreshes a node/agent announcement.
    /// A <c>ttl</c> of <c>0</c> evicts the entry (offline notification).
    /// </summary>
    void Announce(AnnounceFrame frame);

    /// <summary>
    /// Resolves a <c>nwp://</c> target URL to a <see cref="NdpResolveResult"/>.
    /// Returns <c>null</c> when no matching registration is found or all entries have expired.
    /// </summary>
    /// <param name="target">Full <c>nwp://</c> URL, e.g. <c>nwp://api.example.com/products</c>.</param>
    NdpResolveResult? Resolve(string target);

    /// <summary>
    /// Returns all currently live (non-expired) announcements.
    /// </summary>
    IReadOnlyList<AnnounceFrame> GetAll();

    /// <summary>
    /// Returns the live announcement for a specific NID, or <c>null</c> if not present / expired.
    /// </summary>
    AnnounceFrame? GetByNid(string nid);

    /// <summary>
    /// Resolves a <c>nwp://</c> target URL to a <see cref="NdpResolveResult"/>,
    /// falling back to DNS TXT record lookup (NPS-4 §5) when no in-registry entry matches.
    ///
    /// <para>DNS fallback queries <c>_nps-node.{authority}</c> for TXT records in the format
    /// <c>v=nps1 nid=... port=... type=... fp=...</c>.</para>
    /// </summary>
    /// <param name="target">Full <c>nwp://</c> URL, e.g. <c>nwp://api.example.com/products</c>.</param>
    /// <param name="dnsLookup">
    /// DNS TXT resolver to use.  When <c>null</c>, a <see cref="SystemDnsTxtLookup"/> is used.
    /// Inject a fake implementation in unit tests to avoid real network calls.
    /// </param>
    NdpResolveResult? ResolveViaDns(string target, IDnsTxtLookup? dnsLookup = null);
}
