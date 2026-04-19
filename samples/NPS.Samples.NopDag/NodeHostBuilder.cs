// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NPS.NWP.ActionNode;
using NPS.NWP.Extensions;

namespace NPS.Samples.NopDag;

/// <summary>
/// Builds a self-contained Kestrel host hosting one <see cref="IActionNodeProvider"/>
/// on a single action id at <c>PathPrefix="/"</c>. Used by the demo to spin up
/// three independent Action Nodes in one process.
/// </summary>
public static class NodeHostBuilder
{
    public static WebApplication Build<TProvider>(
        string url, string nodeId, string actionId, string description)
        where TProvider : class, IActionNodeProvider
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.SetMinimumLevel(LogLevel.Warning); // keep demo stdout readable

        var actions = new Dictionary<string, ActionSpec>
        {
            [actionId] = new ActionSpec
            {
                Description = description,
                Async       = false,
                Idempotent  = true,
                TimeoutMsDefault = 5_000,
                TimeoutMsMax     = 15_000,
            },
        };

        builder.Services.AddActionNode<TProvider>(o =>
        {
            o.NodeId            = nodeId;
            o.Actions           = actions;
            o.PathPrefix        = "/";
            o.DisplayName       = nodeId;
            o.DefaultTimeoutMs  = 5_000;
            o.MaxTimeoutMs      = 15_000;
        });

        var app = builder.Build();
        app.UseActionNode<TProvider>(o =>
        {
            o.NodeId     = nodeId;
            o.Actions    = actions;
            o.PathPrefix = "/";
        });
        return app;
    }
}
