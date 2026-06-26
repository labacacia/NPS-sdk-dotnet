// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.Daemon.Observability.HealthChecks;

namespace NPS.Tests.Observability;

public sealed class HealthProbeRendererTests
{
    [Fact]
    public void RenderHealthz_ReturnsOkJsonWithoutAspNetHost()
    {
        var response = HealthProbeRenderer.RenderHealthz();

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal("ok", response.Status);
        Assert.Contains("\"status\":\"ok\"", response.Body);
    }

    [Fact]
    public async Task RenderReadyzAsync_ReturnsFirstProbeFailure()
    {
        var probes = new IReadinessProbe[]
        {
            new DelegateReadinessProbe("storage", _ => Task.FromResult<string?>("storage unavailable")),
        };

        var response = await HealthProbeRenderer.RenderReadyzAsync(probes);

        Assert.Equal(503, response.StatusCode);
        Assert.Equal("error", response.Status);
        Assert.Equal("storage unavailable", response.Reason);
        Assert.Contains("\"reason\":\"storage unavailable\"", response.Body);
    }
}
