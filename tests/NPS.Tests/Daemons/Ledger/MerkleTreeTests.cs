// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using NPS.Daemon.Ledger;

namespace NPS.Tests.Daemons.Ledger;

/// <summary>
/// RFC 9162 §2 binary Merkle tree tests. Hash convention:
/// <list type="bullet">
///   <item>Leaf hash:  <c>SHA256(0x00 || leaf_data)</c></item>
///   <item>Node hash:  <c>SHA256(0x01 || left || right)</c></item>
///   <item>Empty tree: <c>SHA256(empty)</c></item>
/// </list>
/// </summary>
public sealed class MerkleTreeTests
{
    [Fact]
    public void EmptyTree_RootIsSha256OfEmpty()
    {
        var root     = MerkleTree.Root(Array.Empty<byte[]>());
        var expected = SHA256.HashData(Array.Empty<byte>());
        Assert.Equal(expected, root);
    }

    [Fact]
    public void SingleLeaf_RootIsThatLeafHash()
    {
        var leaf = MerkleTree.LeafHash(Encoding.ASCII.GetBytes("alpha"));
        var root = MerkleTree.Root(new[] { leaf });
        Assert.Equal(leaf, root);
    }

    [Fact]
    public void TwoLeaves_RootIsNodeHashOfBoth()
    {
        var l0 = MerkleTree.LeafHash(Encoding.ASCII.GetBytes("alpha"));
        var l1 = MerkleTree.LeafHash(Encoding.ASCII.GetBytes("beta"));
        var root = MerkleTree.Root(new[] { l0, l1 });

        var expected = NodeHash(l0, l1);
        Assert.Equal(expected, root);
    }

    [Fact]
    public void FourLeaves_RootIsBalancedTwoTier()
    {
        var leaves = MakeLeaves("a", "b", "c", "d");
        var root   = MerkleTree.Root(leaves);

        var expected = NodeHash(
            NodeHash(leaves[0], leaves[1]),
            NodeHash(leaves[2], leaves[3]));
        Assert.Equal(expected, root);
    }

    [Fact]
    public void FiveLeaves_RootMatchesRfc9162ShapeFor4Plus1()
    {
        // RFC 9162 §2.1: largest power of 2 less than n splits the tree.
        // For n=5, k=4 — tree splits as MTH(0..4) | MTH(4..5).
        var leaves = MakeLeaves("a", "b", "c", "d", "e");
        var root   = MerkleTree.Root(leaves);

        var expectedLeft = NodeHash(
            NodeHash(leaves[0], leaves[1]),
            NodeHash(leaves[2], leaves[3]));
        var expected = NodeHash(expectedLeft, leaves[4]);

        Assert.Equal(expected, root);
    }

    [Fact]
    public void InclusionProof_VerifiesAtEveryIndex()
    {
        // For a 7-leaf tree (asymmetric), the audit path from each index
        // MUST recompute the root.
        var leaves = MakeLeaves("a", "b", "c", "d", "e", "f", "g");
        var root   = MerkleTree.Root(leaves);

        for (int i = 0; i < leaves.Count; i++)
        {
            var path        = MerkleTree.InclusionProof(leaves, i);
            var recomputed  = RecomputeRootFromProof(leaves[i], i, leaves.Count, path);
            Assert.Equal(root, recomputed);
        }
    }

    [Fact]
    public void InclusionProof_OnSingleLeafTree_IsEmpty()
    {
        var leaves = MakeLeaves("only");
        var path   = MerkleTree.InclusionProof(leaves, 0);
        Assert.Empty(path);
    }

    [Fact]
    public void InclusionProof_RejectsOutOfRange()
    {
        var leaves = MakeLeaves("a", "b");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MerkleTree.InclusionProof(leaves, 2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MerkleTree.InclusionProof(leaves, -1));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<byte[]> MakeLeaves(params string[] data) =>
        data.Select(s => MerkleTree.LeafHash(Encoding.ASCII.GetBytes(s))).ToList();

    private static byte[] NodeHash(byte[] left, byte[] right)
    {
        var buf = new byte[1 + left.Length + right.Length];
        buf[0] = 0x01;
        left.CopyTo(buf, 1);
        right.CopyTo(buf, 1 + left.Length);
        return SHA256.HashData(buf);
    }

    /// <summary>
    /// Replays RFC 9162 §2.1.3.2 verification. The audit path is supplied
    /// leaf-to-root (innermost sibling first); we fold each sibling onto
    /// our running hash, deciding left-or-right by the parity of the
    /// remaining leaf-index bits.
    /// </summary>
    private static byte[] RecomputeRootFromProof(
        byte[] leafHash, int index, int treeSize, IReadOnlyList<byte[]> path)
    {
        var fn = index;
        var sn = treeSize - 1;
        var r  = leafHash;

        foreach (var sibling in path)
        {
            if (sn == 0) break;

            if ((fn & 1) == 1 || fn == sn)
            {
                r = NodeHash(sibling, r);
                while ((fn & 1) == 0)
                {
                    fn >>= 1;
                    sn >>= 1;
                }
            }
            else
            {
                r = NodeHash(r, sibling);
            }

            fn >>= 1;
            sn >>= 1;
        }

        return r;
    }
}
