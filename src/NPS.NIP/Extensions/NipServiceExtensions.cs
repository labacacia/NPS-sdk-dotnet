// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NPS.NIP.Acme;
using NPS.NIP.Ca;
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
    /// Registers NIP CA services into the DI container.
    /// Loads (or generates) the CA keypair from the configured key file.
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
        var opts = new NipCaOptions
        {
            CaNid            = string.Empty,
            KeyFilePath      = string.Empty,
            KeyPassphrase    = string.Empty,
            BaseUrl          = string.Empty,
            ConnectionString = string.Empty,
        };
        configure(opts);

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

        // Store — PostgreSQL-backed
        services.AddSingleton<INipCaStore>(_ =>
            new PostgreSqlNipCaStore(opts.ConnectionString));

        // Core CA service
        services.AddSingleton<NipCaService>(sp => new NipCaService(
            opts,
            sp.GetRequiredService<INipCaStore>(),
            sp.GetRequiredService<NipKeyManager>()));

        // ACME server — only registered when enabled
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
        var opts = app.ServiceProvider.GetRequiredService<NipCaOptions>();
        var ca   = app.ServiceProvider.GetRequiredService<NipCaService>();
        NipCaRouter.MapNipCa(app, opts, ca);
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
