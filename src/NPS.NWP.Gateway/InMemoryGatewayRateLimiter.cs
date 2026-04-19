// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace NPS.NWP.Gateway;

/// <summary>
/// Process-local implementation of <see cref="IGatewayRateLimiter"/> using
/// per-consumer sliding-window counters. Adequate for single-instance
/// gateways or testing; replace with a distributed counter (Redis
/// <c>INCRBY</c>, etc.) when deploying across multiple gateway instances.
/// </summary>
public sealed class InMemoryGatewayRateLimiter : IGatewayRateLimiter
{
    private readonly ConcurrentDictionary<string, ConsumerState> _state = new();

    /// <summary>Clock override, primarily for tests. Defaults to <see cref="DateTime.UtcNow"/>.</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    public GatewayRateLimitResult TryAcquire(
        string             consumerKey,
        uint               nptCost,
        GatewayRateLimits? limits)
    {
        if (limits is null ||
            (limits.RequestsPerMinute == 0 &&
             limits.MaxConcurrent     == 0 &&
             limits.NptPerHour        == 0))
        {
            // Nothing to enforce — fast-path succeed. Concurrency counter still
            // updated so Release() is always safe to call.
            var st = _state.GetOrAdd(consumerKey, _ => new ConsumerState());
            Interlocked.Increment(ref st.Concurrent);
            return new GatewayRateLimitResult(true);
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
                    return new GatewayRateLimitResult(false,
                        $"requests_per_minute limit ({limits.RequestsPerMinute}) exceeded.",
                        retry < 1 ? 1 : retry);
                }
            }

            // 2. max concurrent
            if (limits.MaxConcurrent > 0 && state.Concurrent >= limits.MaxConcurrent)
            {
                return new GatewayRateLimitResult(false,
                    $"max_concurrent limit ({limits.MaxConcurrent}) exceeded.", 1);
            }

            // 3. NPT/hour bucket
            if (limits.NptPerHour > 0 && nptCost > 0)
            {
                state.TrimNpt(now);
                var consumed = 0UL;
                foreach (var (_, cost) in state.NptHistory) consumed += cost;
                if (consumed + nptCost > limits.NptPerHour)
                {
                    var oldest = state.NptHistory.Count > 0
                        ? state.NptHistory.Peek().At
                        : now;
                    var retry  = (int)Math.Ceiling((oldest.AddHours(1) - now).TotalSeconds);
                    return new GatewayRateLimitResult(false,
                        $"npt_per_hour limit ({limits.NptPerHour}) exceeded (need {nptCost}, consumed {consumed}).",
                        retry < 1 ? 1 : retry);
                }
                state.NptHistory.Enqueue((now, nptCost));
            }

            // Commit.
            if (limits.RequestsPerMinute > 0) state.RequestTimes.Enqueue(now);
            state.Concurrent++;
            return new GatewayRateLimitResult(true);
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
