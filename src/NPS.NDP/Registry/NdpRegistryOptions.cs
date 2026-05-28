// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NDP.Models;

namespace NPS.NDP.Registry;

/// <summary>
/// Configuration options for <see cref="InMemoryNdpRegistry"/> (NPS-4 §7).
/// </summary>
public sealed class NdpRegistryOptions
{
    /// <summary>
    /// Security profile applied during <c>Announce</c> ingestion (NPS-4 §7.2).
    /// Defaults to <see cref="SecurityProfile.LocalDev"/> (no enforcement).
    /// Use <see cref="SecurityProfile.OrgPrivate"/> or <see cref="SecurityProfile.PublicFederated"/>
    /// in production deployments.
    /// </summary>
    public string SecurityProfile { get; set; } = Models.SecurityProfile.LocalDev;
}
