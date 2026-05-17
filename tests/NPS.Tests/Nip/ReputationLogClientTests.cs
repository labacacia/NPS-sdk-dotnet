// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.NIP.Reputation;
using NSec.Cryptography;

namespace NPS.Tests.Nip;

/// <summary>
/// Phase 2 tests for NPS-RFC-0004: SignedTreeHead, InclusionProof, and
/// <see cref="ReputationLogClient.VerifyInclusion"/> (Merkle inclusion proof
/// verification per RFC 9162 §2.1.3.2).
/// </summary>
public sealed class ReputationLogClientTests
{
    private static readonly JsonSerializerOptions WireOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    // ── Fixture helpers ────────────────────────────────────────────────────────

    private static ReputationLogEntry MakeSigned(string subjectNid = "urn:nps:agent:test:subject-1")
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var unsigned = new ReputationLogEntry
        {
            Version    = 1,
            LogId      = "urn:nps:org:log.test",
            Seq        = 1,
            Timestamp  = "2026-01-01T00:00:00Z",
            SubjectNid = subjectNid,
            Incident   = IncidentType.CertRevoked,
            Severity   = Severity.Info,
            IssuerNid  = "urn:nps:org:issuer.test",
            Signature  = "",
        };
        return ReputationLogEntrySigner.Sign(key, unsigned);
    }

    // Compute the Merkle leaf hash: SHA256(0x00 || canonical_json_of_entry_with_all_fields_sorted)
    private static byte[] LeafHash(ReputationLogEntry entry)
    {
        var json  = CanonicalAllFields(entry);
        var bytes = Encoding.UTF8.GetBytes(json);
        var input = new byte[1 + bytes.Length];
        input[0]  = 0x00;
        bytes.CopyTo(input, 1);
        return SHA256.HashData(input);
    }

    // Compute node hash: SHA256(0x01 || left || right)
    private static byte[] NodeHash(byte[] left, byte[] right)
    {
        var buf = new byte[65];
        buf[0] = 0x01;
        left.CopyTo(buf, 1);
        right.CopyTo(buf, 33);
        return SHA256.HashData(buf);
    }

    // Canonical JSON of entry INCLUDING all fields (no exclusions), keys sorted at every level.
    // This mirrors ReputationLogClient.LeafCanonicalJson exactly.
    private static string CanonicalAllFields(ReputationLogEntry entry)
    {
        var json  = JsonSerializer.Serialize(entry, WireOpts);
        using var doc = JsonDocument.Parse(json);
        var sb    = new StringBuilder();
        WriteSorted(doc.RootElement, sb);
        return sb.ToString();
    }

    private static void WriteSorted(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var props = el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                for (int i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(JsonEncodedText.Encode(props[i].Name)).Append("\":");
                    WriteSorted(props[i].Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var items = el.EnumerateArray().ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteSorted(items[i], sb);
                }
                sb.Append(']');
                break;
            default:
                sb.Append(el.GetRawText());
                break;
        }
    }

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── SignedTreeHead JSON ────────────────────────────────────────────────────

    [Fact]
    public void SignedTreeHead_SerializesWithSnakeCaseFieldNames()
    {
        var sth = new SignedTreeHead
        {
            LogId          = "urn:nps:org:log.test",
            TreeSize       = 42,
            Timestamp      = "2026-01-01T00:00:00Z",
            Sha256RootHash = "abc123",
            Signature      = "ed25519:sig",
        };
        var json = JsonSerializer.Serialize(sth, WireOpts);

        Assert.Contains("\"log_id\":", json);
        Assert.Contains("\"tree_size\":42", json);
        Assert.Contains("\"timestamp\":", json);
        Assert.Contains("\"sha256_root_hash\":", json);
        Assert.Contains("\"signature\":", json);
    }

    [Fact]
    public void SignedTreeHead_RoundTripsThrough_Json()
    {
        var sth = new SignedTreeHead
        {
            LogId          = "urn:nps:org:log.test",
            TreeSize       = 100,
            Timestamp      = "2026-05-17T00:00:00Z",
            Sha256RootHash = "rootHashHere",
            Signature      = "ed25519:sigHere",
        };
        var json   = JsonSerializer.Serialize(sth, WireOpts);
        var parsed = JsonSerializer.Deserialize<SignedTreeHead>(json, WireOpts)!;

        Assert.Equal(sth.LogId,          parsed.LogId);
        Assert.Equal(sth.TreeSize,       parsed.TreeSize);
        Assert.Equal(sth.Timestamp,      parsed.Timestamp);
        Assert.Equal(sth.Sha256RootHash, parsed.Sha256RootHash);
        Assert.Equal(sth.Signature,      parsed.Signature);
    }

    // ── InclusionProof JSON ────────────────────────────────────────────────────

    [Fact]
    public void InclusionProof_SerializesWithSnakeCaseFieldNames()
    {
        var proof = new InclusionProof
        {
            Seq       = 5,
            LeafIndex = 4,
            TreeSize  = 8,
            LeafHash  = "leafHashB64",
            AuditPath = new[] { "step0", "step1" },
        };
        var json = JsonSerializer.Serialize(proof, WireOpts);

        Assert.Contains("\"seq\":5", json);
        Assert.Contains("\"leaf_index\":4", json);
        Assert.Contains("\"tree_size\":8", json);
        Assert.Contains("\"leaf_hash\":", json);
        Assert.Contains("\"audit_path\":", json);
    }

    [Fact]
    public void InclusionProof_RoundTripsThrough_Json()
    {
        var proof = new InclusionProof
        {
            Seq       = 99,
            LeafIndex = 98,
            TreeSize  = 200,
            LeafHash  = "abc",
            AuditPath = new[] { "x", "y", "z" },
        };
        var json   = JsonSerializer.Serialize(proof, WireOpts);
        var parsed = JsonSerializer.Deserialize<InclusionProof>(json, WireOpts)!;

        Assert.Equal(proof.Seq,       parsed.Seq);
        Assert.Equal(proof.LeafIndex, parsed.LeafIndex);
        Assert.Equal(proof.TreeSize,  parsed.TreeSize);
        Assert.Equal(proof.LeafHash,  parsed.LeafHash);
        Assert.Equal(proof.AuditPath, parsed.AuditPath);
    }

    // ── VerifyInclusion — single-leaf tree ────────────────────────────────────

    [Fact]
    public void VerifyInclusion_SingleLeaf_EmptyAuditPath_Passes()
    {
        var entry = MakeSigned();
        var lh    = LeafHash(entry);

        var proof = new InclusionProof
        {
            Seq       = entry.Seq,
            LeafIndex = 0,
            TreeSize  = 1,
            LeafHash  = B64Url(lh),
            AuditPath = Array.Empty<string>(),
        };
        var sth = new SignedTreeHead
        {
            LogId          = entry.LogId,
            TreeSize       = 1,
            Timestamp      = entry.Timestamp,
            Sha256RootHash = B64Url(lh),   // root == leaf for a 1-leaf tree
            Signature      = "ed25519:placeholder",
        };

        Assert.True(ReputationLogClient.VerifyInclusion(proof, sth, entry));
    }

    // ── VerifyInclusion — two-leaf tree ───────────────────────────────────────

    [Fact]
    public void VerifyInclusion_TwoLeafTree_BothLeavesPasse()
    {
        var entryA = MakeSigned("urn:nps:agent:test:A");
        var entryB = MakeSigned("urn:nps:agent:test:B");

        var lhA  = LeafHash(entryA);
        var lhB  = LeafHash(entryB);
        var root = NodeHash(lhA, lhB);

        var sth = new SignedTreeHead
        {
            LogId          = "urn:nps:org:log.test",
            TreeSize       = 2,
            Timestamp      = "2026-01-01T00:00:00Z",
            Sha256RootHash = B64Url(root),
            Signature      = "ed25519:placeholder",
        };

        // Proof for leaf 0 (A): sibling is B
        var proofA = new InclusionProof
        {
            Seq       = entryA.Seq,
            LeafIndex = 0,
            TreeSize  = 2,
            LeafHash  = B64Url(lhA),
            AuditPath = new[] { B64Url(lhB) },
        };

        // Proof for leaf 1 (B): sibling is A
        var proofB = new InclusionProof
        {
            Seq       = entryB.Seq,
            LeafIndex = 1,
            TreeSize  = 2,
            LeafHash  = B64Url(lhB),
            AuditPath = new[] { B64Url(lhA) },
        };

        Assert.True(ReputationLogClient.VerifyInclusion(proofA, sth, entryA));
        Assert.True(ReputationLogClient.VerifyInclusion(proofB, sth, entryB));
    }

    // ── VerifyInclusion — tamper detection ────────────────────────────────────

    [Fact]
    public void VerifyInclusion_ReturnsFalse_WhenEntryTampered()
    {
        var entry   = MakeSigned();
        var lh      = LeafHash(entry);
        var proof   = new InclusionProof { Seq = 1, LeafIndex = 0, TreeSize = 1, LeafHash = B64Url(lh), AuditPath = Array.Empty<string>() };
        var sth     = new SignedTreeHead { LogId = "x", TreeSize = 1, Timestamp = "t", Sha256RootHash = B64Url(lh), Signature = "ed25519:x" };

        // Tamper subject_nid — changes the canonical JSON, thus the leaf hash
        var tampered = entry with { SubjectNid = "urn:nps:agent:evil:1" };

        Assert.False(ReputationLogClient.VerifyInclusion(proof, sth, tampered));
    }

    [Fact]
    public void VerifyInclusion_ReturnsFalse_WhenRootWrong()
    {
        var entry = MakeSigned();
        var lh    = LeafHash(entry);
        var proof = new InclusionProof { Seq = 1, LeafIndex = 0, TreeSize = 1, LeafHash = B64Url(lh), AuditPath = Array.Empty<string>() };
        var sth   = new SignedTreeHead { LogId = "x", TreeSize = 1, Timestamp = "t", Sha256RootHash = B64Url(new byte[32]), Signature = "ed25519:x" };

        Assert.False(ReputationLogClient.VerifyInclusion(proof, sth, entry));
    }

    [Fact]
    public void VerifyInclusion_ReturnsFalse_WhenAuditPathCorrupted()
    {
        var entryA = MakeSigned("urn:nps:agent:test:A");
        var entryB = MakeSigned("urn:nps:agent:test:B");
        var lhA    = LeafHash(entryA);
        var lhB    = LeafHash(entryB);
        var root   = NodeHash(lhA, lhB);

        var sth = new SignedTreeHead { LogId = "x", TreeSize = 2, Timestamp = "t", Sha256RootHash = B64Url(root), Signature = "ed25519:x" };
        var proof = new InclusionProof
        {
            Seq       = 1,
            LeafIndex = 0,
            TreeSize  = 2,
            LeafHash  = B64Url(lhA),
            AuditPath = new[] { B64Url(new byte[32]) }, // wrong sibling
        };

        Assert.False(ReputationLogClient.VerifyInclusion(proof, sth, entryA));
    }

    [Fact]
    public void VerifyInclusion_ReturnsFalse_WhenLeafHashMismatch()
    {
        var entry = MakeSigned();
        var lh    = LeafHash(entry);
        // Supply a wrong leaf_hash in the proof
        var wrongLh = new byte[32];
        wrongLh[0]  = 0xFF;
        var proof = new InclusionProof { Seq = 1, LeafIndex = 0, TreeSize = 1, LeafHash = B64Url(wrongLh), AuditPath = Array.Empty<string>() };
        var sth   = new SignedTreeHead { LogId = "x", TreeSize = 1, Timestamp = "t", Sha256RootHash = B64Url(wrongLh), Signature = "ed25519:x" };

        Assert.False(ReputationLogClient.VerifyInclusion(proof, sth, entry));
    }

    [Fact]
    public void VerifyInclusion_ReturnsFalse_ForGarbageBase64InAuditPath()
    {
        var entry = MakeSigned();
        var lh    = LeafHash(entry);
        var proof = new InclusionProof
        {
            Seq       = 1,
            LeafIndex = 0,
            TreeSize  = 2,
            LeafHash  = B64Url(lh),
            AuditPath = new[] { "!!!not-base64!!!" },
        };
        var sth = new SignedTreeHead { LogId = "x", TreeSize = 2, Timestamp = "t", Sha256RootHash = B64Url(lh), Signature = "ed25519:x" };

        Assert.False(ReputationLogClient.VerifyInclusion(proof, sth, entry));
    }

    // ── VerifyInclusion — null argument guards ────────────────────────────────

    [Fact]
    public void VerifyInclusion_NullArguments_Throw()
    {
        var entry = MakeSigned();
        var lh    = LeafHash(entry);
        var proof = new InclusionProof { Seq = 1, LeafIndex = 0, TreeSize = 1, LeafHash = B64Url(lh), AuditPath = Array.Empty<string>() };
        var sth   = new SignedTreeHead { LogId = "x", TreeSize = 1, Timestamp = "t", Sha256RootHash = B64Url(lh), Signature = "ed25519:x" };

        Assert.Throws<ArgumentNullException>(() => ReputationLogClient.VerifyInclusion(null!, sth,   entry));
        Assert.Throws<ArgumentNullException>(() => ReputationLogClient.VerifyInclusion(proof, null!, entry));
        Assert.Throws<ArgumentNullException>(() => ReputationLogClient.VerifyInclusion(proof, sth,   null!));
    }

    // ── Leaf hash is deterministic ─────────────────────────────────────────────

    [Fact]
    public void LeafHash_IsDeterministic_ForSameEntry()
    {
        var entry = MakeSigned();
        var h1    = LeafHash(entry);
        var h2    = LeafHash(entry);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void LeafHash_DiffersForDifferentEntries()
    {
        var a = MakeSigned("urn:nps:agent:test:A");
        var b = MakeSigned("urn:nps:agent:test:B");
        Assert.NotEqual(LeafHash(a), LeafHash(b));
    }

    // ── Four-leaf tree: tests deeper audit paths ──────────────────────────────

    [Fact]
    public void VerifyInclusion_FourLeafTree_AllLeavesPas()
    {
        // Build 4 entries
        var entries = Enumerable.Range(0, 4)
            .Select(i => MakeSigned($"urn:nps:agent:test:{i}"))
            .ToArray();
        var hashes = entries.Select(LeafHash).ToArray();

        // Level 1: pair nodes
        var n01  = NodeHash(hashes[0], hashes[1]);
        var n23  = NodeHash(hashes[2], hashes[3]);
        var root = NodeHash(n01, n23);

        var sth = new SignedTreeHead
        {
            LogId          = "urn:nps:org:log.test",
            TreeSize       = 4,
            Timestamp      = "2026-01-01T00:00:00Z",
            Sha256RootHash = B64Url(root),
            Signature      = "ed25519:placeholder",
        };

        // leaf 0: sibling=hash[1], then sibling=n23
        var proof0 = new InclusionProof { Seq = entries[0].Seq, LeafIndex = 0, TreeSize = 4, LeafHash = B64Url(hashes[0]), AuditPath = new[] { B64Url(hashes[1]), B64Url(n23) } };
        // leaf 1: sibling=hash[0], then sibling=n23
        var proof1 = new InclusionProof { Seq = entries[1].Seq, LeafIndex = 1, TreeSize = 4, LeafHash = B64Url(hashes[1]), AuditPath = new[] { B64Url(hashes[0]), B64Url(n23) } };
        // leaf 2: sibling=hash[3], then sibling=n01
        var proof2 = new InclusionProof { Seq = entries[2].Seq, LeafIndex = 2, TreeSize = 4, LeafHash = B64Url(hashes[2]), AuditPath = new[] { B64Url(hashes[3]), B64Url(n01) } };
        // leaf 3: sibling=hash[2], then sibling=n01
        var proof3 = new InclusionProof { Seq = entries[3].Seq, LeafIndex = 3, TreeSize = 4, LeafHash = B64Url(hashes[3]), AuditPath = new[] { B64Url(hashes[2]), B64Url(n01) } };

        Assert.True(ReputationLogClient.VerifyInclusion(proof0, sth, entries[0]));
        Assert.True(ReputationLogClient.VerifyInclusion(proof1, sth, entries[1]));
        Assert.True(ReputationLogClient.VerifyInclusion(proof2, sth, entries[2]));
        Assert.True(ReputationLogClient.VerifyInclusion(proof3, sth, entries[3]));
    }
}
