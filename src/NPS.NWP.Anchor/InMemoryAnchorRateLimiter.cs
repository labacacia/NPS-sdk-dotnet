// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace NPS.NWP.Anchor;

/// <summary>
/// Process-local implementation of <see cref="IAnchorRateLimiter"/> using
/// per-consumer sliding-window counters. Adequate for single-instance
/// anchors or testing; replace with a distributed counter (Redis
/// <c>INCRBY</c>, etc.) when deploying across multiple anchor instances.
/// </summary>
public sealed class InMemoryAnchorRateLimiter : IAnchorRateLimiter
{
    private readonly ConcurrentDictionary<string, ConsumerState> _state = new();

    /// <summary>Clock override, primarily for tests. Defaults to <see cref="DateTime.UtcNow"/>.</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    public AnchorRateLimitResult TryAcquire(
        string             consumerKey,
        uint               cgnCost,
        AnchorRateLimits? limits)
    {
        if (limits is null ||
            (limits.RequestsPerMinute == 0 &&
             limits.MaxConcurrent     == 0 &&
             limits.CgnPerHour        == 0))
        {
            // Nothing to enforce — fast-path succeed. Concurrency counter still
            // updated so Release() is always safe to call.
            var st = _state.GetOrAdd(consumerKey, _ => new ConsumerState());
            Interlocked.Increment(ref st.Concurrent);
            return new AnchorRateLimitResult(true);
        }

        var now = Clock();
        var state = _state.GetOrAdd(consumerKey, _ => new ConsumerState());

        lock (state.Gate)
        {
            // 1. requests/minute window
            if (limits.RequestsPerMinute > 0)
            {
                state.TrimRequests(now);
                if (state.RequestTimes.Count >= limits.RequestsPerMinute)
                {
                    var oldest = state.RequestTimes.Peek();
                    var retry  = (int)Math.Ceiling((oldest.AddMinutes(1) - now).TotalSeconds);
                    return new AnchorRateLimitResult(false,
                        $"requests_per_minute limit ({limits.RequestsPerMinute}) exceeded.",
                        retry < 1 ? 1 : retry);
                }
            }

            // 2. max concurrent
            if (limits.MaxConcurrent > 0 && state.Concurrent >= limits.MaxConcurrent)
            {
                return new AnchorRateLimitResult(false,
                    $"max_concurrent limit ({limits.MaxConcurrent}) exceeded.", 1);
            }

            // 3. CGN/hour bucket
            if (limits.CgnPerHour > 0 && cgnCost > 0)
            {
                state.TrimNpt(now);
                var consumed = 0UL;
                foreach (var (_, cost) in state.NptHistory) consumed += cost;
                if (consumed + cgnCost > limits.CgnPerHour)
                {
                    var oldest = state.NptHistory.Count > 0
                        ? state.NptHistory.Peek().At
                        : now;
                    var retry  = (int)Math.Ceiling((oldest.AddHours(1) - now).TotalSeconds);
                    return new AnchorRateLimitResult(false,
                        $"cgn_per_hour limit ({limits.CgnPerHour}) exceeded (need {cgnCost}, consumed {consumed}).",
                        retry < 1 ? 1 : retry);
                }
                state.NptHistory.Enqueue((now, cgnCost));
            }

            // Commit.
            if (limits.RequestsPerMinute > 0) state.RequestTimes.Enqueue(now);
            state.Concurrent++;
            return new AnchorRateLimitResult(true);
        }
    }

    public void Release(string consumerKey)
    {
        if (_state.TryGetValue(consumerKey, out var state))
        {
            // Interlocked is fine even though acquires take the lock — we only
            // decrement here so lock-free wins simplicity.
            Interlocked.Decrement(ref state.Concurrent);
            if (state.Concurrent < 0) Interlocked.Exchange(ref state.Concurrent, 0);
        }
    }

    private sealed class ConsumerState
    {
        public readonly object Gate = new();
        public readonly Queue<DateTime>             RequestTimes = new();
        public readonly Queue<(DateTime At, uint Cost)> NptHistory   = new();
        public int Concurrent;

        public void TrimRequests(DateTime now)
        {
            var cutoff = now.AddMinutes(-1);
            while (RequestTimes.Count > 0 && RequestTimes.Peek() < cutoff)
                RequestTimes.Dequeue();
        }

        public void TrimNpt(DateTime now)
        {
            var cutoff = now.AddHours(-1);
            while (NptHistory.Count > 0 && NptHistory.Peek().At < cutoff)
                NptHistory.Dequeue();
        }
    }
}
