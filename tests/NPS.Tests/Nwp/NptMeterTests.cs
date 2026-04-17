// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NWP.MemoryNode;

namespace NPS.Tests.Nwp;

public sealed class NptMeterTests
{
    // ── Measure(ReadOnlySpan<byte>) ───────────────────────────────────────────

    [Theory]
    [InlineData(0,  0)]
    [InlineData(1,  1)]
    [InlineData(4,  1)]
    [InlineData(5,  2)]
    [InlineData(8,  2)]
    [InlineData(9,  3)]
    [InlineData(100, 25)]
    public void MeasureBytes_CeilDiv4(int byteCount, uint expected)
    {
        var bytes = new byte[byteCount];
        Assert.Equal(expected, NptMeter.Measure(bytes.AsSpan()));
    }

    // ── Measure(string?) ─────────────────────────────────────────────────────

    [Fact]
    public void MeasureNull_ReturnsZero() =>
        Assert.Equal(0u, NptMeter.Measure((string?)null));

    [Fact]
    public void MeasureEmptyString_ReturnsZero() =>
        Assert.Equal(0u, NptMeter.Measure(string.Empty));

    [Fact]
    public void MeasureAscii_CeilDiv4()
    {
        // "test" = 4 UTF-8 bytes → ceil(4/4) = 1
        Assert.Equal(1u, NptMeter.Measure("test"));
        // "hello" = 5 UTF-8 bytes → ceil(5/4) = 2
        Assert.Equal(2u, NptMeter.Measure("hello"));
    }

    [Fact]
    public void MeasureMultiByte_UsesUtf8ByteCount()
    {
        // "中" = 3 UTF-8 bytes → ceil(3/4) = 1
        Assert.Equal(1u, NptMeter.Measure("中"));
        // "中文" = 6 UTF-8 bytes → ceil(6/4) = 2
        Assert.Equal(2u, NptMeter.Measure("中文"));
    }

    // ── MeasureJson<T> ────────────────────────────────────────────────────────

    [Fact]
    public void MeasureJson_UsesSerializedByteLength()
    {
        var obj = new { name = "test" };
        var expected = NptMeter.Measure(System.Text.Json.JsonSerializer.Serialize(obj));
        Assert.Equal(expected, NptMeter.MeasureJson(obj));
    }

    // ── MeasureRows ───────────────────────────────────────────────────────────

    [Fact]
    public void MeasureRows_SumsAllRows()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "alpha" },
            new Dictionary<string, object?> { ["id"] = 2L, ["name"] = "beta"  },
        };
        var total = NptMeter.MeasureRows(rows);
        Assert.True(total > 0);

        uint manual = 0;
        foreach (var r in rows) manual += NptMeter.MeasureJson(r);
        Assert.Equal(manual, total);
    }

    [Fact]
    public void MeasureRows_EmptyList_ReturnsZero()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        Assert.Equal(0u, NptMeter.MeasureRows(rows));
    }
}
