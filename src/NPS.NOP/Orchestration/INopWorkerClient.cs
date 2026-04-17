// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NOP.Frames;

namespace NPS.NOP.Orchestration;

/// <summary>
/// Abstraction for dispatching <see cref="DelegateFrame"/>s to Worker Agents
/// and receiving <see cref="AlignStreamFrame"/> results (NPS-5 §3.2, §3.4).
/// <para>
/// Implement this interface to connect the Orchestrator to real agents
/// (e.g. via HTTP/NWP, in-process, or mock in tests).
/// </para>
/// </summary>
public interface INopWorkerClient
{
    /// <summary>
    /// Dispatches a <see cref="DelegateFrame"/> to the target Worker Agent and
    /// returns a stream of <see cref="AlignStreamFrame"/> messages.
    /// The final frame has <see cref="AlignStreamFrame.IsFinal"/> set to <c>true</c>.
    /// </summary>
    IAsyncEnumerable<AlignStreamFrame> DelegateAsync(DelegateFrame frame, CancellationToken ct = default);

    /// <summary>
    /// Sends a lightweight preflight probe to <paramref name="agentNid"/> to confirm
    /// resource availability before committing to full execution (NPS-5 §4).
    /// </summary>
    /// <param name="agentNid">Target Worker Agent NID.</param>
    /// <param name="action">The action URL the agent will be asked to perform.</param>
    /// <param name="estimatedNpt">Estimated NPT budget for the operation.</param>
    /// <param name="requiredCapabilities">Capability identifiers the agent must support.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PreflightResult> PreflightAsync(
        string                    agentNid,
        string                    action,
        long                      estimatedNpt        = 0,
        IReadOnlyList<string>?    requiredCapabilities = null,
        CancellationToken         ct                  = default);
}
