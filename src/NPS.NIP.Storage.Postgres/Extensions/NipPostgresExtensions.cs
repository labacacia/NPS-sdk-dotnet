// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using NPS.NIP.Ca;
using NPS.NIP.Ca.Ra;
using NPS.NIP.Extensions;
using NPS.NIP.Storage;

namespace NPS.NIP.Extensions;

/// <summary>
/// DI registration extensions for the PostgreSQL NIP CA storage backend.
/// </summary>
public static class NipPostgresExtensions
{
    /// <summary>
    /// Registers NIP CA services using a PostgreSQL certificate store.
    /// <para>
    /// <see cref="NipCaOptions.ConnectionString"/> must be set to a valid PostgreSQL
    /// connection string. For SQLite use
    /// <c>LabAcacia.NPS.NIP.Storage.Sqlite</c> and call <c>AddNipCaWithSqlite()</c>,
    /// or supply a custom <see cref="INipCaStore"/> via
    /// <c>AddNipCa(configure, INipCaStore)</c>.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to configure <see cref="NipCaOptions"/>.</param>
    /// <param name="generateKeyIfMissing">
    /// When <c>true</c>, generates a new CA keypair if <see cref="NipCaOptions.KeyFilePath"/>
    /// does not exist. Useful for first-run / development.
    /// Defaults to <c>false</c> (fail-fast in production).
    /// </param>
    public static IServiceCollection AddNipCaWithPostgres(
        this IServiceCollection services,
        Action<NipCaOptions> configure,
        bool generateKeyIfMissing = false)
    {
        // Build options once to read ConnectionString, then pass the prebuilt store
        // to the core AddNipCa overload which handles all other DI registration.
        var opts = new NipCaOptions
        {
            CaNid         = string.Empty,
            KeyFilePath   = string.Empty,
            KeyPassphrase = string.Empty,
            BaseUrl       = string.Empty,
        };
        configure(opts);

        if (string.IsNullOrWhiteSpace(opts.ConnectionString))
            throw new InvalidOperationException(
                "NipCaOptions.ConnectionString must be set when using AddNipCaWithPostgres(). " +
                "Provide a valid PostgreSQL connection string.");

        var certStore = new PostgreSqlNipCaStore(opts.ConnectionString);
        var raStore   = new PostgreSqlNipRaStore(opts.ConnectionString);
        raStore.MigrateAsync().GetAwaiter().GetResult();

        services.AddNipCa(configure, certStore, generateKeyIfMissing);
        services.AddSingleton<IBootstrapTokenStore>(raStore);
        services.AddSingleton<IPendingStore>(raStore);
        return services;
    }
}
