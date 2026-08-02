using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using Xunit;

namespace NPS.Tests.Nop;

/// <summary>NOP v0.7 result_ttl_seconds retention semantics.</summary>
public sealed class NopResultRetentionTests
{
    private static readonly DateTime Done = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    private static NopTaskRecord Record(uint? ttl = null, DateTime? completedAt = null) => new()
    {
        TaskId    = "t1",
        Frame     = new TaskFrame
        {
            TaskId = "t1",
            Dag    = new TaskDag { Nodes = [], Edges = [] },
            ResultTtlSeconds = ttl ?? 3600,
        },
        StartedAt   = Done.AddMinutes(-5),
        CompletedAt = completedAt,
    };

    [Fact]
    public void Inflight_tasks_never_expire() =>
        Assert.False(NopResultRetention.IsExpired(Record(completedAt: null), Done.AddYears(1)));

    [Fact]
    public void Fresh_result_is_readable() =>
        Assert.False(NopResultRetention.IsExpired(Record(completedAt: Done), Done.AddMinutes(30)));

    [Fact]
    public void Result_expires_after_ttl() =>
        Assert.True(NopResultRetention.IsExpired(Record(completedAt: Done), Done.AddSeconds(3601)));

    [Fact]
    public void Custom_ttl_is_honored() =>
        Assert.True(NopResultRetention.IsExpired(Record(ttl: 60, completedAt: Done), Done.AddSeconds(61)));
}
