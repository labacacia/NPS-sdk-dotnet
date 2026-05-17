// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace NPS.NIP.Reputation;

/// <summary>
/// Merkle inclusion proof for a <see cref="ReputationLogEntry"/>.
/// NPS-RFC-0004 §5.3 / RFC 9162 §2.1.3.2.
/// </summary>
public sealed record InclusionProof
{
    /// <summary>Entry sequence number this proof covers.</summary>
    [JsonPropertyName("seq")]
    public required ulong Seq { get; init; }

    /// <summary>Zero-based leaf index in the Merkle tree.</summary>
    [JsonPropertyName("leaf_index")]
    public required ulong LeafIndex { get; init; }

    /// <summary>Tree size at the time the proof was computed.</summary>
    [JsonPropertyName("tree_size")]
    public required ulong TreeSize { get; init; }

    /// <summary>Base64url-encoded SHA-256 hash of the Merkle leaf.</summary>
    [JsonPropertyName("leaf_hash")]
    public required string LeafHash { get; init; }

    /// <summary>
    /// Ordered list of base64url-encoded sibling hashes from leaf to root
    /// (RFC 9162 §2.1.3.2).
    /// </summary>
    [JsonPropertyName("audit_path")]
    public required IReadOnlyList<string> AuditPath { get; init; }
}
