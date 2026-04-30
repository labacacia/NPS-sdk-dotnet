// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NWP.Anchor.Topology;

/// <summary>
/// Server-side contract for the <c>topology.snapshot</c> / <c>topology.stream</c>
/// reserved query types defined in NPS-2 §12. <see cref="AnchorNodeMiddleware"/>
/// resolves an instance of this interface from DI when a <c>/query</c> or
/// <c>/subscribe</c> request carries <c>type = "topology.*"</c>.
///
/// <para>Implementations own the cluster registry: they ingest NDP <c>Announce</c>
/// frames (or other discovery sources), maintain a per-Anchor monotonic
/// <c>version</c> counter, and (for streams) buffer recent events for replay.</para>
///
/// <para>An in-memory reference implementation —
/// <see cref="InMemoryAnchorTopologyService"/> — ships with this assembly and
/// is sufficient for single-node deployments and conformance testing.</para>
/// </summary>
public interface IAnchorTopologyService
{
    /// <summary>
    /// Service the <c>topology.snapshot</c> query. The result is serialized into
    /// the response <c>CapsFrame.data[0]</c> by the middleware.
    /// </summary>
    Task<TopologySnapshot> GetSnapshotAsync(
        TopologySnapshotRequest request,
        CancellationToken       cancel = default);

    /// <summary>
    /// Service the <c>topology.stream</c> subscription. The middleware streams
    /// each yielded event as one NDJSON line over <c>/subscribe</c>.
    ///
    /// <para>Cancellation of <paramref name="cancel"/> (typically driven by the
    /// HTTP request abort token) MUST terminate the iteration cleanly.</para>
    /// </summary>
    IAsyncEnumerable<TopologyEvent> SubscribeAsync(
        TopologyStreamRequest request,
        CancellationToken     cancel = default);

    /// <summary>
    /// Identity of the responding Anchor. Surfaced in
    /// <see cref="TopologySnapshot.AnchorNid"/>.
    /// </summary>
    string AnchorNid { get; }
}
