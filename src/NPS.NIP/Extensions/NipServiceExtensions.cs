// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NPS.NIP.Acme;
using NPS.NIP.Ca;
using NPS.NIP.Ca.Ra;
using NPS.NIP.Crypto;
using NPS.NIP.Http;
using NPS.NIP.Storage;
using NPS.NIP.Verification;

namespace NPS.NIP.Extensions;

/// <summary>
/// DI and pipeline registration extensions for the NIP CA Server.
/// <para>
/// Embed in any ASP.NET Core app:
/// <code>
/// builder.Services.AddNipCa(opts => { opts.CaNid = "..."; ... });
/// // ...
/// app.MapNipCa();
/// </code>
/// </para>
/// </summary>
public static class NipServiceExtensions
{
    /// <summary>
    /// Registers NIP CA services into the DI container using a PostgreSQL store.
    /// Loads (or generates) the CA keypair from the configured key file.
    /// <para>
    /// <see cref="NipCaOptions.ConnectionString"/> must be set to a valid PostgreSQL connection string.
    /// For SQLite or custom stores use <see cref="AddNipCa(IServiceCollection,Action{NipCaOptions},INipCaStore,bool)"/>
    /// or <see cref="AddNipCaWithSqlite"/>.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to configure <see cref="NipCaOptions"/>.</param>
    /// <param name="generateKeyIfMissing">
    /// When <c>true</c>, generates a new CA keypair if <see cref="NipCaOptions.KeyFilePath"/>
    /// does not exist. Useful for first-run / development.
    /// Defaults to <c>false</c> (fail-fast in production).
    /// </param>
    public static IServiceCollection AddNipCa(
        this IServiceCollection services,
        Action<NipCaOptions> configure,
        bool generateKeyIfMissing = false)
    {
        var opts = BuildOptions(configure);

        if (string.IsNullOrWhiteSpace(opts.ConnectionString))
            throw new InvalidOperationException(
                "NipCaOptions.ConnectionString must be set when using the default PostgreSQL backend. " +
                "For SQLite use AddNipCaWithSqlite(), or supply a custom store via " +
                "AddNipCa(configure, INipCaStore store).");

        var store = new PostgreSqlNipCaStore(opts.ConnectionString);
        return RegisterCore(services, opts, store, generateKeyIfMissing);
    }

    /// <summary>
    /// Registers NIP CA services into the DI container using a caller-supplied store.
    /// Loads (or generates) the CA keypair from the configured key file.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to configure <see cref="NipCaOptions"/>.</param>
    /// <param name="store">
    /// The <see cref="INipCaStore"/> implementation to use for certificate persistence.
    /// Use <c>SqliteNipCaStore.OpenAsync(connectionString)</c> for SQLite,
    /// or supply any custom implementation.
    /// </param>
    /// <param name="generateKeyIfMissing">
    /// When <c>true</c>, generates a new CA keypair if <see cref="NipCaOptions.KeyFilePath"/>
    /// does not exist.
    /// </param>
    public static IServiceCollection AddNipCa(
        this IServiceCollection services,
        Action<NipCaOptions> configure,
        INipCaStore store,
        bool generateKeyIfMissing = false)
    {
        var opts = BuildOptions(configure);
        return RegisterCore(services, opts, store, generateKeyIfMissing);
    }

    /// <summary>
    /// Registers NIP CA services into the DI container using a SQLite store.
    /// Runs schema migrations synchronously during startup.
    /// Loads (or generates) the CA keypair from the configured key file.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to configure <see cref="NipCaOptions"/>.</param>
    /// <param name="sqliteConnectionString">
    /// SQLite connection string, e.g. <c>"Data Source=nip-ca.db"</c>.
    /// The database file is created if it does not exist.
    /// </param>
    /// <param name="generateKeyIfMissing">
    /// When <c>true</c>, generates a new CA keypair if <see cref="NipCaOptions.KeyFilePath"/>
    /// does not exist.
    /// </param>
    public static IServiceCollection AddNipCaWithSqlite(
        this IServiceCollection services,
        Action<NipCaOptions> configure,
        string sqliteConnectionString,
        bool generateKeyIfMissing = false)
    {
        var opts  = BuildOptions(configure);
        var store = SqliteNipCaStore.OpenAsync(sqliteConnectionString)
            .GetAwaiter().GetResult();
        return RegisterCore(services, opts, store, generateKeyIfMissing);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static NipCaOptions BuildOptions(Action<NipCaOptions> configure)
    {
        var opts = new NipCaOptions
        {
            CaNid         = string.Empty,
            KeyFilePath   = string.Empty,
            KeyPassphrase = string.Empty,
            BaseUrl       = string.Empty,
        };
        configure(opts);
        return opts;
    }

    private static IServiceCollection RegisterCore(
        IServiceCollection services,
        NipCaOptions opts,
        INipCaStore store,
        bool generateKeyIfMissing)
    {
        services.AddSingleton(opts);

        // Key manager — singleton holding the in-memory Ed25519 key
        services.AddSingleton<NipKeyManager>(sp =>
        {
            var km  = new NipKeyManager();
            var log = sp.GetRequiredService<ILogger<NipKeyManager>>();

            if (!File.Exists(opts.KeyFilePath))
            {
                if (!generateKeyIfMissing)
                    throw new InvalidOperationException(
                        $"NIP CA key file not found: {opts.KeyFilePath}. " +
                        "Generate it with NipKeyManager.Generate() or set generateKeyIfMissing=true.");

                log.LogWarning("CA key file not found — generating new keypair at {Path}", opts.KeyFilePath);
                km.Generate(opts.KeyFilePath, opts.KeyPassphrase);
            }
            else
            {
                log.LogInformation("Loading CA keypair from {Path}", opts.KeyFilePath);
                km.Load(opts.KeyFilePath, opts.KeyPassphrase);
            }
            return km;
        });

        services.AddSingleton<INipCaStore>(store);

        // RA stores — registered unconditionally; only used if the matching tier is selected
        services.AddSingleton<IBootstrapTokenStore, InMemoryBootstrapTokenStore>();
        services.AddSingleton<IPendingStore>(new InMemoryPendingStore(opts.PendingQueueMaxAge));

        services.AddSingleton<NipCaService>(sp => new NipCaService(
            opts,
            sp.GetRequiredService<INipCaStore>(),
            sp.GetRequiredService<NipKeyManager>()));

        if (opts.AcmeEnabled)
        {
            services.AddSingleton<AcmeServer>(sp =>
            {
                var ca   = sp.GetRequiredService<NipCaService>();
                var keys = sp.GetRequiredService<NipKeyManager>();
                var acmeOpts = new AcmeServerOptions
                {
                    PathPrefix       = opts.AcmePathPrefix,
                    CaNid            = opts.CaNid,
                    CertValidityDays = opts.AgentCertValidityDays,
                };
                return new AcmeServer(acmeOpts, ca, keys, ca.CaRootCert);
            });
        }

        return services;
    }

    /// <summary>
    /// Registers the Node-side identity verifier (<see cref="NipIdentVerifier"/>) into the DI container.
    /// Call this on Nodes that need to authenticate incoming Agent <c>IdentFrame</c>s.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to configure <see cref="NipVerifierOptions"/>.</param>
    public static IServiceCollection AddNipVerifier(
        this IServiceCollection services,
        Action<NipVerifierOptions> configure)
    {
        var opts = new NipVerifierOptions { TrustedIssuers = new Dictionary<string, string>() };
        configure(opts);
        services.AddSingleton(opts);
        services.AddSingleton<NipIdentVerifier>(sp => new NipIdentVerifier(
            opts,
            sp.GetService<IHttpClientFactory>(),
            sp.GetService<ILogger<NipIdentVerifier>>()));
        return services;
    }

    /// <summary>
    /// Maps all NIP CA API routes onto the application's endpoint router.
    /// Must be called after <c>app.UseRouting()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapNipCa(this IEndpointRouteBuilder app)
    {
        var opts                = app.ServiceProvider.GetRequiredService<NipCaOptions>();
        var ca                  = app.ServiceProvider.GetRequiredService<NipCaService>();
        var bootstrapTokenStore = app.ServiceProvider.GetService<IBootstrapTokenStore>();
        var pendingStore        = app.ServiceProvider.GetService<IPendingStore>();
        NipCaRouter.MapNipCa(app, opts, ca, bootstrapTokenStore, pendingStore);
        return app;
    }

    /// <summary>
    /// Maps all NIP CA API routes onto a <see cref="WebApplication"/>.
    /// </summary>
    public static WebApplication MapNipCa(this WebApplication app)
    {
        ((IEndpointRouteBuilder)app).MapNipCa();
        return app;
    }

    /// <summary>
    /// Mounts the ACME middleware when <see cref="NipCaOptions.AcmeEnabled"/> is true.
    /// Call after <c>app.MapNipCa()</c>.
    /// </summary>
    public static WebApplication UseNipAcme(this WebApplication app)
    {
        var opts = app.Services.GetRequiredService<NipCaOptions>();
        if (!opts.AcmeEnabled) return app;

        var acme = app.Services.GetRequiredService<AcmeServer>();
        acme.MapEndpoints(app);
        return app;
    }
}
