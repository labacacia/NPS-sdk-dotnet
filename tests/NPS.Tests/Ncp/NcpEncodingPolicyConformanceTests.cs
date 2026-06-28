// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.Core;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Ncp;

namespace NPS.Tests.Ncp;

public sealed class NcpEncodingPolicyConformanceTests
{
    [Fact]
    public void EncodingPolicyVectors_AreEnforced()
    {
        var fixture = LoadFixture();

        foreach (var testCase in fixture.Vectors)
        {
            var policy = NcpEncodingPolicy.FromEnabledEncodings(
                ParseEncoding(testCase.Input.Policy.DefaultEncoding),
                testCase.Input.Policy.EnabledEncodings);
            var header = new FrameHeader(
                ParseFrameType(testCase.Input.InboundFrame.FrameType),
                (FrameFlags)ParseByte(testCase.Input.InboundFrame.Flags),
                PayloadLength: 0);

            if (testCase.Expected.Decision == "accept")
            {
                policy.EnsureAllows(header);
                continue;
            }

            var ex = Assert.Throws<NpsEncodingUnsupportedException>(() => policy.EnsureAllows(header));
            Assert.Equal(testCase.Expected.Status, ex.NpsStatusCode);
            Assert.Equal(testCase.Expected.Error, ex.ProtocolErrorCode);
        }
    }

    private static EncodingPolicyFixture LoadFixture()
    {
        var path = FindRepoFile("spec/conformance/ncp/encoding_policy_vectors.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EncodingPolicyFixture>(json)!;
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Unable to locate {relativePath} from {AppContext.BaseDirectory}.");
    }

    private static EncodingTier ParseEncoding(string value) => value switch
    {
        "json" => EncodingTier.Json,
        "msgpack" => EncodingTier.MsgPack,
        "binary_vector.v1" => EncodingTier.BinaryVector,
        _ => throw new InvalidOperationException($"Unknown encoding token '{value}'."),
    };

    private static FrameType ParseFrameType(string value) => (FrameType)ParseByte(value);

    private static byte ParseByte(string value)
    {
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return byte.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private sealed record EncodingPolicyFixture(
        [property: JsonPropertyName("vectors")] IReadOnlyList<EncodingPolicyCase> Vectors);

    private sealed record EncodingPolicyCase(
        [property: JsonPropertyName("input")] EncodingPolicyInput Input,
        [property: JsonPropertyName("expected")] EncodingPolicyExpected Expected);

    private sealed record EncodingPolicyInput(
        [property: JsonPropertyName("policy")] EncodingPolicyDefinition Policy,
        [property: JsonPropertyName("inbound_frame")] EncodingPolicyFrame InboundFrame);

    private sealed record EncodingPolicyDefinition(
        [property: JsonPropertyName("default_encoding")] string DefaultEncoding,
        [property: JsonPropertyName("enabled_encodings")] IReadOnlyList<string> EnabledEncodings);

    private sealed record EncodingPolicyFrame(
        [property: JsonPropertyName("frame_type")] string FrameType,
        [property: JsonPropertyName("flags")] string Flags);

    private sealed record EncodingPolicyExpected(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("decision")] string Decision);
}
