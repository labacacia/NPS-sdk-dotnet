// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPS.Daemon.Ledger;
using NPS.NIP.Reputation;

namespace NPS.Tests.Daemons.Ledger;

/// <summary>
/// Behavioural tests for <see cref="LedgerStore"/>: assigns sequence numbers
/// monotonically, computes RFC 9162 leaf hashes consistent with what a
/// client would recompute from the returned canonical body, persists across
/// reopens.
/// </summary>
public sealed class LedgerStoreTests
{
    private static ReputationLogEntry MakeEntry(string subject = "urn:nps:agent:test", string issuer = "urn:nps:agent:obs") =>
        new()
        {
            Version     = 1,
            LogId       = "urn:nps:log:operator-test",
            Seq         = 0,
            Timestamp   = "1970-01-01T00:00:00Z",
            SubjectNid  = subject,
            Incident    = IncidentType.RateLimitViolation,
            Severity    = Severity.Minor,
            IssuerNid   = issuer,
            Signature   = "ed25519:placeholder",
        };

    [Fact]
    public void Append_AssignsSeqMonotonically_StartingAtOne()
    {
        using var s = LedgerStore.CreateInMemory();
        var a = s.Append(MakeEntry());
        var b = s.Append(MakeEntry());
        var c = s.Append(MakeEntry());

        Assert.Equal(1UL, a.Seq);
        Assert.Equal(2UL, b.Seq);
        Assert.Equal(3UL, c.Seq);
    }

    [Fact]
    public void Append_RewritesTimestamp_PreservesEverythingElse()
    {
        using var s = LedgerStore.CreateInMemory();
        var input = MakeEntry();
        var stored = s.Append(input);

        Assert.NotEqual("1970-01-01T00:00:00Z", stored.Timestamp);
        Assert.Equal(input.SubjectNid, stored.SubjectNid);
        Assert.Equal(input.IssuerNid,  stored.IssuerNid);
        Assert.Equal(input.Signature,  stored.Signature);
    }

    [Fact]
    public void Count_TracksAppendedEntries()
    {
        using var s = LedgerStore.CreateInMemory();
        Assert.Equal(0L, s.Count());
        s.Append(MakeEntry());
        s.Append(MakeEntry());
        Assert.Equal(2L, s.Count());
    }

    [Fact]
    public void Query_FiltersBySubjectAndSince()
    {
        using var s = LedgerStore.CreateInMemory();
        var a = s.Append(MakeEntry("urn:nps:agent:alice"));
        var b = s.Append(MakeEntry("urn:nps:agent:bob"));
        var c = s.Append(MakeEntry("urn:nps:agent:alice"));

        var aliceAll = s.Query("urn:nps:agent:alice", 0);
        Assert.Equal(2, aliceAll.Count);

        var aliceAfterFirst = s.Query("urn:nps:agent:alice", a.Seq);
        Assert.Single(aliceAfterFirst);
        Assert.Equal(c.Seq, aliceAfterFirst[0].Seq);

        var allSinceB = s.Query(null, b.Seq);
        Assert.Single(allSinceB);
    }

    [Fact]
    public void LeafHash_MatchesSha256OfCanonicalBody()
    {
        using var s   = LedgerStore.CreateInMemory();
        var stored    = s.Append(MakeEntry());
        var hashes    = s.LoadAllLeafHashes();

        // Recompute the hash from the stored entry using the same
        // canonicalisation the store uses (snake_case, no whitespace,
        // null fields elided when writing).
        var jsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var bodyJson = JsonSerializer.Serialize(stored, jsonOpts);
        var expected = SHA256.HashData(
            new byte[] { 0x00 }
                .Concat(Encoding.UTF8.GetBytes(bodyJson))
                .ToArray());

        Assert.Single(hashes);
        Assert.Equal(expected, hashes[0]);
    }

    [Fact]
    public void GetLeafHashAndIndex_ReturnsCorrectIndex()
    {
        using var s = LedgerStore.CreateInMemory();
        var a = s.Append(MakeEntry());
        var b = s.Append(MakeEntry());
        var c = s.Append(MakeEntry());

        var (_,    indexA) = s.GetLeafHashAndIndex(a.Seq);
        var (hashB, indexB) = s.GetLeafHashAndIndex(b.Seq);
        var (_,    indexC) = s.GetLeafHashAndIndex(c.Seq);

        Assert.Equal(0, indexA);
        Assert.Equal(1, indexB);
        Assert.Equal(2, indexC);

        Assert.NotNull(hashB);
        Assert.Equal(s.LoadAllLeafHashes()[1], hashB);
    }

    [Fact]
    public void GetLeafHashAndIndex_ReturnsNullForUnknownSeq()
    {
        using var s = LedgerStore.CreateInMemory();
        var (hash, _) = s.GetLeafHashAndIndex(999);
        Assert.Null(hash);
    }

    [Fact]
    public void Persists_AcrossReopens_OnFileBackedStore()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"ledger-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new LedgerStore(path))
            {
                first.Append(MakeEntry());
                first.Append(MakeEntry());
            }

            using var second = new LedgerStore(path);
            Assert.Equal(2L, second.Count());
            // Continues numbering from where it left off.
            var third = second.Append(MakeEntry());
            Assert.Equal(3UL, third.Seq);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
