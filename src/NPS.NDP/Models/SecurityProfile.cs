// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.NDP.Models;

/// <summary>
/// Registry security profiles (NPS-4 §7.2).
/// Configured on the registry; enforced during <c>Announce</c> ingestion.
/// </summary>
public static class SecurityProfile
{
    /// <summary>
    /// No address or signature enforcement. Suitable for local development and testing.
    /// All announcements are accepted as-is.
    /// </summary>
    public const string LocalDev       = "local-dev";

    /// <summary>
    /// Private network only. All announced addresses MUST be RFC-1918 or loopback.
    /// Public IP addresses are rejected. Suitable for internal organisational networks.
    /// </summary>
    public const string OrgPrivate     = "org-private";

    /// <summary>
    /// Public federation. All announced addresses MUST be globally routable (no RFC-1918 or loopback).
    /// Suitable for nodes participating in the public NPS federation.
    /// </summary>
    public const string PublicFederated = "public-federated";
}
