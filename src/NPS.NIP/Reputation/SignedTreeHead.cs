// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace NPS.NIP.Reputation;

/// <summary>
/// Signed Tree Head (STH) produced by the reputation-log operator.
/// NPS-RFC-0004 §5.2 / RFC 9162 §2.1.3.
/// </summary>
public sealed record SignedTreeHead
{
    /// <summary>NID of the log operator.</summary>
    [JsonPropertyName("log_id")]
    public required string LogId { get; init; }

    /// <summary>Number of entries currently in the tree.</summary>
    [JsonPropertyName("tree_size")]
    public required ulong TreeSize { get; init; }

    /// <summary>Log operator's timestamp for this STH. RFC 3339 UTC.</summary>
    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    /// <summary>Base64url-encoded SHA-256 root hash of the Merkle tree.</summary>
    [JsonPropertyName("sha256_root_hash")]
    public required string Sha256RootHash { get; init; }

    /// <summary>
    /// <c>ed25519:{base64url}</c> operator signature over the canonical form of
    /// this STH (with <c>signature</c> excluded).
    /// </summary>
    [JsonPropertyName("signature")]
    public required string Signature { get; init; }
}
