// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NOP.Models;

namespace NPS.Tests.Nop;

public class RetryPolicyTests
{
    [Fact]
    public void FixedBackoff_ReturnsConstantDelay()
    {
        var policy = new RetryPolicy { Backoff = BackoffStrategy.Fixed, InitialDelayMs = 2000 };

        Assert.Equal(2000u, policy.ComputeDelayMs(0));
        Assert.Equal(2000u, policy.ComputeDelayMs(1));
        Assert.Equal(2000u, policy.ComputeDelayMs(5));
    }

    [Fact]
    public void LinearBackoff_ScalesLinearly()
    {
        var policy = new RetryPolicy { Backoff = BackoffStrategy.Linear, InitialDelayMs = 1000 };

        Assert.Equal(1000u, policy.ComputeDelayMs(0));
        Assert.Equal(2000u, policy.ComputeDelayMs(1));
        Assert.Equal(3000u, policy.ComputeDelayMs(2));
    }

    [Fact]
    public void ExponentialBackoff_DoublesEachAttempt()
    {
        var policy = new RetryPolicy { Backoff = BackoffStrategy.Exponential, InitialDelayMs = 1000 };

        Assert.Equal(1000u, policy.ComputeDelayMs(0));
        Assert.Equal(2000u, policy.ComputeDelayMs(1));
        Assert.Equal(4000u, policy.ComputeDelayMs(2));
        Assert.Equal(8000u, policy.ComputeDelayMs(3));
    }

    [Fact]
    public void ExponentialBackoff_CapsAtMaxDelay()
    {
        var policy = new RetryPolicy
        {
            Backoff = BackoffStrategy.Exponential,
            InitialDelayMs = 1000,
            MaxDelayMs = 5000,
        };

        Assert.Equal(4000u, policy.ComputeDelayMs(2));
        Assert.Equal(5000u, policy.ComputeDelayMs(3));
        Assert.Equal(5000u, policy.ComputeDelayMs(10));
    }

    [Fact]
    public void Defaults_ArePerSpec()
    {
        var policy = new RetryPolicy();

        Assert.Equal(BackoffStrategy.Exponential, policy.Backoff);
        Assert.Equal(1000u, policy.InitialDelayMs);
        Assert.Equal(30000u, policy.MaxDelayMs);
        Assert.Null(policy.RetryOn);
    }
}
