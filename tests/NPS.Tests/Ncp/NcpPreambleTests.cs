// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using NPS.Core;
using NPS.Core.Exceptions;
using NPS.Core.Ncp;

namespace NPS.Tests.Ncp;

/// <summary>
/// Phase 1 reference tests for NPS-RFC-0001 (NCP native-mode connection
/// preamble). These exercise the helper class directly; full transport-level
/// tests will land alongside the future <c>NPS.Native</c> module.
/// </summary>
public sealed class NcpPreambleTests
{
    [Fact]
    public void Bytes_AreExactlyTheSpecConstant()
    {
        // Per RFC §4.1: "NPS/1.0\n" → 4E 50 53 2F 31 2E 30 0A
        var expected = new byte[] { 0x4E, 0x50, 0x53, 0x2F, 0x31, 0x2E, 0x30, 0x0A };

        Assert.Equal(8, NcpPreamble.Length);
        Assert.Equal("NPS/1.0\n", NcpPreamble.Literal);
        Assert.True(NcpPreamble.Bytes.SequenceEqual(expected));
        Assert.Equal(expected, NcpPreamble.ToArray());
    }

    [Fact]
    public void Bytes_AreEightBytes_NeverGrowOrShrink()
    {
        // Guards against accidental editing of the constant — wire breakage.
        Assert.Equal(NcpPreamble.Length, NcpPreamble.Bytes.Length);
        Assert.Equal(NcpPreamble.Length, NcpPreamble.ToArray().Length);
        Assert.Equal(NcpPreamble.Length, Encoding.ASCII.GetByteCount(NcpPreamble.Literal));
    }

    [Fact]
    public void ToArray_ReturnsACopy_NotASharedBuffer()
    {
        // Mutating the returned array MUST NOT corrupt the next caller.
        var copy = NcpPreamble.ToArray();
        copy[0] = 0xFF;

        var second = NcpPreamble.ToArray();
        Assert.Equal((byte)0x4E, second[0]);
    }

    [Fact]
    public void Matches_ReturnsTrue_ForExactPreamble()
    {
        Assert.True(NcpPreamble.Matches(NcpPreamble.Bytes));
    }

    [Fact]
    public void Matches_ReturnsTrue_WhenPreambleIsAtTheStartOfALongerBuffer()
    {
        // Common case: client pipelines the preamble + first HelloFrame in one write.
        var combined = new byte[16];
        NcpPreamble.Bytes.CopyTo(combined.AsSpan());
        combined[8] = 0x06;       // HelloFrame type byte begins right after.

        Assert.True(NcpPreamble.Matches(combined));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void Matches_ReturnsFalse_OnShortReads(int length)
    {
        var truncated = NcpPreamble.Bytes.Slice(0, length).ToArray();
        Assert.False(NcpPreamble.Matches(truncated));
    }

    [Fact]
    public void TryValidate_AcceptsExactPreamble()
    {
        Assert.True(NcpPreamble.TryValidate(NcpPreamble.Bytes, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void TryValidate_RejectsShortRead_WithExplanatoryReason()
    {
        Assert.False(NcpPreamble.TryValidate(stackalloc byte[3], out var reason));
        Assert.Contains("short read", reason);
        Assert.Contains("3/8", reason);
    }

    [Fact]
    public void TryValidate_RejectsArbitraryGarbage()
    {
        var garbage = Encoding.ASCII.GetBytes("GET / HTT");
        Assert.False(NcpPreamble.TryValidate(garbage, out var reason));
        Assert.DoesNotContain("future", reason);
        Assert.Contains("not speaking NPS", reason);
    }

    [Fact]
    public void TryValidate_RejectsNullByteFlood()
    {
        var zeros = new byte[NcpPreamble.Length];
        Assert.False(NcpPreamble.TryValidate(zeros, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void TryValidate_FlagsFutureMajorVersionDistinctly()
    {
        // RFC §4.1: "NPS/2.0\n" — peer self-identifies as NPS but with an
        // unsupported major. Caller MAY emit a one-time diagnostic.
        var futureMajor = Encoding.ASCII.GetBytes("NPS/2.0\n");
        Assert.False(NcpPreamble.TryValidate(futureMajor, out var reason));
        Assert.Contains("future-major", reason);
    }

    [Fact]
    public void Validate_Throws_WithReasonExposed()
    {
        var bad = Encoding.ASCII.GetBytes("BADXXXXX");
        var ex = Assert.Throws<NcpPreambleInvalidException>(() => NcpPreamble.Validate(bad));

        Assert.Equal(NcpErrorCodes.PreambleInvalid, ex.ProtocolErrorCode);
        Assert.Equal(NpsStatusCodes.ProtoPreambleInvalid, ex.NpsStatusCode);
        Assert.NotEmpty(ex.Reason);
    }

    [Fact]
    public void Validate_DoesNotThrow_OnValidPreamble()
    {
        // Should be a no-op; an exception here would block the happy path.
        NcpPreamble.Validate(NcpPreamble.Bytes);
    }

    [Fact]
    public async Task WriteAsync_EmitsExactlyTheConstantBytes()
    {
        await using var stream = new MemoryStream();
        await NcpPreamble.WriteAsync(stream);

        Assert.Equal(NcpPreamble.Length, stream.Length);
        var written = stream.ToArray();
        Assert.True(written.AsSpan().SequenceEqual(NcpPreamble.Bytes));
    }

    [Fact]
    public async Task WriteAsync_RoundTripsThroughTryValidate()
    {
        await using var stream = new MemoryStream();
        await NcpPreamble.WriteAsync(stream);

        Assert.True(NcpPreamble.TryValidate(stream.ToArray(), out _));
    }

    [Fact]
    public async Task WriteAsync_RejectsNullStream()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await NcpPreamble.WriteAsync(null!));
    }

    [Fact]
    public void StatusAndErrorCodeConstants_MatchSpec()
    {
        // Defends against accidental rename — these strings are wire-visible
        // in SDK telemetry and MUST stay aligned with spec/error-codes.md
        // and spec/status-codes.md.
        Assert.Equal("NCP-PREAMBLE-INVALID", NcpPreamble.ErrorCode);
        Assert.Equal("NPS-PROTO-PREAMBLE-INVALID", NcpPreamble.StatusCode);
        Assert.Equal(NcpErrorCodes.PreambleInvalid, NcpPreamble.ErrorCode);
        Assert.Equal(NpsStatusCodes.ProtoPreambleInvalid, NcpPreamble.StatusCode);
    }

    [Fact]
    public void Timeouts_MatchSpec()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), NcpPreamble.ReadTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(500), NcpPreamble.CloseDeadline);
    }
}
