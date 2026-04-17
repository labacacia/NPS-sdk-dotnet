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
}
