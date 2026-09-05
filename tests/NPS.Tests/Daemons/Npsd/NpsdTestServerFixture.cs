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
///   - in-memory SQLite stores by default, or caller-owned file stores for restart tests
///   - an HttpClient configured against the test server
/// </summary>
internal sealed class NpsdTestServerFixture : IAsyncDisposable
{
    private readonly bool _deleteDataDirOnDispose;

    public string DataDir { get; }
    public NpsdOptions Options { get; }
    public WebApplication App { get; }
    public HttpClient Client { get; }

    private NpsdTestServerFixture(
        string dataDir,
        NpsdOptions options,
        WebApplication app,
        HttpClient client,
        bool deleteDataDirOnDispose)
    {
        DataDir = dataDir;
        Options = options;
        App = app;
        Client = client;
        _deleteDataDirOnDispose = deleteDataDirOnDispose;
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

        var baseOpts = new NpsdOptions
        {
            DataDir = dataDir,
            NdpAnnounceEnabled = false,
        };
        return await CreateCoreAsync(
            dataDir,
            tweak(baseOpts),
            useInMemorySqlite: true,
            deleteDataDirOnDispose: true);
    }

    /// <summary>
    /// Starts a TestServer backed by the caller-owned data directory. Disposing
    /// the fixture closes the stores but deliberately preserves their files so a
    /// replacement host can verify restart continuity.
    /// </summary>
    public static Task<NpsdTestServerFixture> CreatePersistentAsync(
        string dataDir,
        Func<NpsdOptions, NpsdOptions>? tweak = null)
    {
        Directory.CreateDirectory(dataDir);
        var baseOptions = new NpsdOptions
        {
            DataDir = dataDir,
            NdpAnnounceEnabled = false,
        };
        return CreateCoreAsync(
            dataDir,
            tweak?.Invoke(baseOptions) ?? baseOptions,
            useInMemorySqlite: false,
            deleteDataDirOnDispose: false);
    }

    private static async Task<NpsdTestServerFixture> CreateCoreAsync(
        string dataDir,
        NpsdOptions options,
        bool useInMemorySqlite,
        bool deleteDataDirOnDispose)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        NpsdHost.WireServices(builder.Services, options, useInMemorySqlite);
        var app = builder.Build();
        NpsdHost.WireRoutes(app, options);

        await app.StartAsync();
        var client = app.GetTestClient();

        return new NpsdTestServerFixture(
            dataDir,
            options,
            app,
            client,
            deleteDataDirOnDispose);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { await App.StopAsync(); } catch { /* ignore */ }
        await App.DisposeAsync();
        if (_deleteDataDirOnDispose)
        {
            try { Directory.Delete(DataDir, recursive: true); } catch { /* leave for ops */ }
        }
    }
}
