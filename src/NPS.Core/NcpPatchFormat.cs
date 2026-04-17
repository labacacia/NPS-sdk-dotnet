// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Core;

/// <summary>
/// <see cref="Frames.Ncp.DiffFrame.PatchFormat"/> value constants (NPS-1 §4.2).
/// </summary>
public static class NcpPatchFormat
{
    /// <summary>
    /// Default format. <c>patch</c> is an RFC 6902 JSON Patch array.
    /// Compatible with all encoding tiers.
    /// </summary>
    public const string JsonPatch    = "json_patch";

    /// <summary>
    /// Compact binary format. <c>binary_patch</c> contains a changed-fields bitset
    /// followed by MsgPack-encoded new values.
    /// MUST only be used in Tier-2 (MsgPack) frames.
    /// </summary>
    public const string BinaryBitset = "binary_bitset";
}
