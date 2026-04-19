// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NWP.ActionNode;
using NPS.NWP.Frames;
using NPS.NWP.MemoryNode;

namespace NPS.NWP.ComplexNode;

/// <summary>
/// Local behaviour of a Complex Node. Mirrors the Memory + Action Node surfaces so that
/// a single provider can serve both <c>/query</c> and <c>/invoke</c>.
///
/// <para>
/// A Complex Node MAY expose data only (leave <c>ExecuteAsync</c> unused), actions only
/// (leave <c>QueryAsync</c> unused), or both. When the Node's role is purely to aggregate
/// child nodes, implement <see cref="NullComplexNodeProvider"/>.
/// </para>
/// </summary>
public interface IComplexNodeProvider
{
    /// <summary>
    /// Answer the local part of a <c>/query</c>. Implementations SHOULD honour the
    /// node's <see cref="ComplexNodeOptions.Schema"/>. Return an empty result when the
    /// Complex Node does not expose any local data.
    /// </summary>
    Task<MemoryNodeQueryResult> QueryAsync(
        QueryFrame        frame,
        ComplexNodeOptions options,
        CancellationToken  ct = default);

    /// <summary>
    /// Execute a local action. Only invoked for <c>action_id</c>s declared in
    /// <see cref="ComplexNodeOptions.Actions"/> — unknown ids return
    /// <c>NWP-ACTION-NOT-FOUND</c> before reaching the provider.
    /// </summary>
    Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame       frame,
        ActionContext     context,
        CancellationToken ct = default);
}

/// <summary>
/// Convenience provider for Complex Nodes that only aggregate child nodes and have no
/// local data or actions of their own. <see cref="QueryAsync"/> returns an empty row
/// set; <see cref="ExecuteAsync"/> throws — it MUST NOT be called because the
/// middleware refuses any <c>action_id</c> not declared in <c>Actions</c>.
/// </summary>
public sealed class NullComplexNodeProvider : IComplexNodeProvider
{
    public Task<MemoryNodeQueryResult> QueryAsync(
        QueryFrame frame, ComplexNodeOptions options, CancellationToken ct = default)
        => Task.FromResult(new MemoryNodeQueryResult
        {
            Rows = Array.Empty<IReadOnlyDictionary<string, object?>>(),
        });

    public Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame, ActionContext context, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "NullComplexNodeProvider does not handle actions. " +
            "Register actions only if your Complex Node has a real provider.");
}
