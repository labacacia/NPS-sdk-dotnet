// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NPS.Daemon.Npsd;

namespace NPS.Tests.Daemons.Npsd;

/// <summary>
/// Wires up an in-memory <c>npsd</c> via <see cref="TestServer"/>.
/// Each instance carries:
///   - a fresh temp data directory (root keypair lives there)
///   - an in-memory SQLite SubNidStore (no on-disk pollution)
///   - an HttpClient configured against the test server
/// </summary>
internal sealed class NpsdTestServerFixture : IAsyncDisposable
{
    public string         DataDir { get; }
    public NpsdOptions    Options { get; }
    public WebApplication App     { get; }
    public HttpClient     Client  { get; }

    private NpsdTestServerFixture(string dataDir, NpsdOptions options, WebApplication app, HttpClient client)
    {
        DataDir = dataDir;
        Options = options;
        App     = app;
        Client  = client;
    }

    public static Task<NpsdTestServerFixture> CreateAsync()
        => CreateWithOptionsAsync(opts => opts);

    /// <summary>
    /// Build the fixture with caller-supplied option overrides. The temp
    /// <c>DataDir</c> is provided to the callback; callers <c>with { ... }</c>
    /// it to tweak other knobs.
    /// </summary>
    public static async Task<NpsdTestServerFixture> CreateWithOptionsAsync(Func<NpsdOptions, NpsdOptions> tweak)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"npsd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);

        var baseOpts = new NpsdOptions { DataDir = dataDir };
        var opts     = tweak(baseOpts);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        NpsdHost.WireServices(builder.Services, opts, useInMemorySqlite: true);
        var app = builder.Build();
        NpsdHost.WireRoutes(app, opts);

        await app.StartAsync();
        var client = app.GetTestClient();

        return new NpsdTestServerFixture(dataDir, opts, app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { await App.StopAsync(); } catch { /* ignore */ }
        await App.DisposeAsync();
        try { Directory.Delete(DataDir, recursive: true); } catch { /* leave for ops */ }
    }
}
