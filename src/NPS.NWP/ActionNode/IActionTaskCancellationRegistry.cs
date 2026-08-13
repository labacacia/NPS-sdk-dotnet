// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace NPS.NWP.ActionNode;

/// <summary>Process-local cancellation handles for background Action Node tasks.</summary>
public interface IActionTaskCancellationRegistry
{
    bool TryRegister(string taskId, CancellationTokenSource source);
    bool Cancel(string taskId);
    void Remove(string taskId);
}

public sealed class InMemoryActionTaskCancellationRegistry : IActionTaskCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sources = new();

    public bool TryRegister(string taskId, CancellationTokenSource source) =>
        _sources.TryAdd(taskId, source);

    public bool Cancel(string taskId)
    {
        if (!_sources.TryGetValue(taskId, out var source)) return false;
        source.Cancel();
        return true;
    }

    public void Remove(string taskId) => _sources.TryRemove(taskId, out _);
}
