// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using NPS.NIP.Ca;
using NPS.NIP.Extensions;
using NPS.NIP.Storage;

namespace NPS.NIP.Extensions;

/// <summary>
/// DI registration extensions for the SQLite NIP CA storage backend.
/// </summary>
public static class NipSqliteExtensions
{
    /// <summary>
    /// Registers NIP CA services using a SQLite certificate store.
    /// Runs schema migrations synchronously during startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback to configure <see cref="NipCaOptions"/>.</param>
    /// <param name="sqliteConnectionString">
    /// SQLite connection string, e.g. <c>"Data Source=nip-ca.db"</c>.
    /// The database file is created and migrated if it does not exist.
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
        var store = SqliteNipCaStore.OpenAsync(sqliteConnectionString)
            .GetAwaiter().GetResult();
        return services.AddNipCa(configure, store, generateKeyIfMissing);
    }
}
