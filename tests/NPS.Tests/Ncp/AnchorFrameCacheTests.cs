// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NPS.Core;
using NPS.Core.Caching;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Tests.Ncp;

public sealed class AnchorFrameCacheTests : IDisposable
{
    private readonly MemoryCache         _memCache = new(Options.Create(new MemoryCacheOptions()));
    private          AnchorFrameCache    Cache     => new(_memCache);

    public void Dispose() => _memCache.Dispose();

    private static FrameSchema MakeSchema(params string[] fieldNames) => new()
    {
        Fields = fieldNames.Select(n => new SchemaField(n, "string")).ToList()
    };

    private static AnchorFrame MakeAnchor(FrameSchema schema, uint ttl = 3600)
    {
        var anchorId = AnchorFrameCache.ComputeAnchorId(schema);
        return new AnchorFrame { AnchorId = anchorId, Schema = schema, Ttl = ttl };
    }

    // ── Set and TryGet ───────────────────────────────────────────────────────

    [Fact]
    public void Set_NewEntry_ReturnsAnchorId()
    {
        var schema   = MakeSchema("id", "name");
        var frame    = MakeAnchor(schema);
        var cache    = Cache;
        var anchorId = cache.Set(frame);

        Assert.StartsWith("sha256:", anchorId);
        Assert.Equal(71, anchorId.Length); // "sha256:" + 64 hex chars
    }

    [Fact]
    public void TryGet_AfterSet_ReturnsFrame()
    {
        var schema   = MakeSchema("id", "name");
        var frame    = MakeAnchor(schema);
        var cache    = Cache;
        var anchorId = cache.Set(frame);

        var found = cache.TryGet(anchorId, out var retrieved);

        Assert.True(found);
        Assert.NotNull(retrieved);
        Assert.Equal(anchorId, retrieved.AnchorId);
    }

    [Fact]
    public void TryGet_UnknownAnchorId_ReturnsFalse()
    {
        var cache = Cache;
        var found = cache.TryGet("sha256:" + new string('0', 64), out var retrieved);

        Assert.False(found);
        Assert.Null(retrieved);
    }

    // ── GetRequired ──────────────────────────────────────────────────────────

    [Fact]
    public void GetRequired_ExistingEntry_ReturnsFrame()
    {
        var schema   = MakeSchema("id", "price");
        var frame    = MakeAnchor(schema);
        var cache    = Cache;
        var anchorId = cache.Set(frame);

        var result = cache.GetRequired(anchorId);
        Assert.Equal(anchorId, result.AnchorId);
    }

    [Fact]
    public void GetRequired_MissingEntry_ThrowsNpsAnchorNotFoundException()
    {
        var cache    = Cache;
        var anchorId = "sha256:" + new string('9', 64);

        var ex = Assert.Throws<NpsAnchorNotFoundException>(() => cache.GetRequired(anchorId));
        Assert.Equal(anchorId, ex.AnchorId);
        Assert.Equal(NpsStatusCodes.ClientNotFound, ex.NpsStatusCode);
    }

    // ── Idempotency ──────────────────────────────────────────────────────────

    [Fact]
    public void Set_SameSchemaSetTwice_IsIdempotent()
    {
        var schema = MakeSchema("id", "name");
        var cache  = Cache;
        var id1    = cache.Set(MakeAnchor(schema));
        var id2    = cache.Set(MakeAnchor(schema)); // same schema → same id

        Assert.Equal(id1, id2);
    }

    // ── Anchor poisoning ─────────────────────────────────────────────────────

    [Fact]
    public void Set_DifferentSchemaForSameAnchorId_ThrowsNpsAnchorPoisonException()
    {
        var schemaA = MakeSchema("id", "name");
        var schemaB = MakeSchema("id", "email"); // different fields → different anchor_id
        var cache   = Cache;

        // Force anchor_id collision by using schemaA's anchor_id with schemaB's content
        var anchorId = AnchorFrameCache.ComputeAnchorId(schemaA);
        cache.Set(new AnchorFrame { AnchorId = anchorId, Schema = schemaA, Ttl = 3600 });

        var poisoned = new AnchorFrame { AnchorId = anchorId, Schema = schemaB, Ttl = 3600 };
        var ex = Assert.Throws<NpsAnchorPoisonException>(() => cache.Set(poisoned));

        Assert.Equal(anchorId, ex.AnchorId);
        Assert.Equal(NpsStatusCodes.ClientBadFrame, ex.NpsStatusCode);
    }

    // ── ComputeAnchorId ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeAnchorId_DifferentFieldOrder_SameResult()
    {
        var schemaAB = new FrameSchema { Fields = [new SchemaField("a", "string"), new SchemaField("b", "uint64")] };
        var schemaBA = new FrameSchema { Fields = [new SchemaField("b", "uint64"), new SchemaField("a", "string")] };

        var idAB = AnchorFrameCache.ComputeAnchorId(schemaAB);
        var idBA = AnchorFrameCache.ComputeAnchorId(schemaBA);

        Assert.Equal(idAB, idBA);
    }

    [Fact]
    public void ComputeAnchorId_DifferentFields_DifferentResult()
    {
        var schemaA = MakeSchema("id",   "name");
        var schemaB = MakeSchema("code", "label");

        Assert.NotEqual(
            AnchorFrameCache.ComputeAnchorId(schemaA),
            AnchorFrameCache.ComputeAnchorId(schemaB));
    }

    [Fact]
    public void ComputeAnchorId_ProducesSha256Prefix()
    {
        var id = AnchorFrameCache.ComputeAnchorId(MakeSchema("x"));
        Assert.StartsWith("sha256:", id);
        Assert.Equal(71, id.Length);
    }
}
