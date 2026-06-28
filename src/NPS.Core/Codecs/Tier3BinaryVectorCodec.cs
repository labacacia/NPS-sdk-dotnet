// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
using MessagePack.Resolvers;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Registry;

namespace NPS.Core.Codecs;

/// <summary>
/// Tier-3 codec: BinaryVector v1.
/// Metadata is MessagePack; dense vectors are carried as little-endian float32
/// segments and referenced from the metadata map.
/// </summary>
public sealed class Tier3BinaryVectorCodec : IFrameCodec
{
    private static readonly byte[] Magic = "NPBV"u8.ToArray();
    private const byte Version = 1;
    private const int PrefixSize = 16;
    private const string VectorSearchKey = "vector_search";
    private const string VectorKey = "vector";
    private const string MarkerKey = "$nps_binary_vector";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = false,
    };

    private static readonly MessagePackSerializerOptions MsgPackOpts =
        MessagePackSerializerOptions.Standard
            .WithResolver(ContractlessStandardResolver.Instance)
            .WithCompression(MessagePackCompression.None);

    public byte[] Encode(IFrame frame)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(frame, frame.GetType(), JsonOpts);
            using var doc = JsonDocument.Parse(json);

            var metadata = ConvertElement(doc.RootElement) as Dictionary<string, object?>
                           ?? throw BinaryVectorError(
                               NcpErrorCodes.BinaryVectorMalformed,
                               "Tier-3 BinaryVector metadata root must be an object.");

            var vectors = new List<float[]>();
            ExtractVectorSearchVector(metadata, vectors);

            if (vectors.Count > ushort.MaxValue)
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorMalformed,
                    $"Tier-3 BinaryVector supports at most {ushort.MaxValue} vectors per frame.");

            var metadataBytes = MessagePackSerializer.Serialize(metadata, MsgPackOpts);
            checked
            {
                var segmentBytes = 0;
                foreach (var vector in vectors)
                {
                    segmentBytes += sizeof(uint);
                    segmentBytes += vector.Length * sizeof(float);
                }

                var payload = new byte[PrefixSize + metadataBytes.Length + segmentBytes];
                Magic.CopyTo(payload, 0);
                payload[4] = Version;
                payload[5] = 0;
                BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), (ushort)vectors.Count);
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), (uint)metadataBytes.Length);
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12), 0);
                metadataBytes.CopyTo(payload.AsSpan(PrefixSize));

                var offset = PrefixSize + metadataBytes.Length;
                foreach (var vector in vectors)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), (uint)vector.Length);
                    offset += sizeof(uint);
                    foreach (var value in vector)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset), value);
                        offset += sizeof(float);
                    }
                }

                return payload;
            }
        }
        catch (NpsCodecException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NpsCodecException($"Tier-3 BinaryVector encode failed for {frame.FrameType}.", ex);
        }
    }

    public IFrame Decode(FrameType type, ReadOnlySpan<byte> payload, FrameRegistry registry)
    {
        var clrType = registry.Resolve(type);
        try
        {
            if (payload.Length < PrefixSize)
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorTruncated,
                    $"Tier-3 BinaryVector payload too short: {payload.Length} bytes.");

            if (!payload[..4].SequenceEqual(Magic))
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorMalformed,
                    "Tier-3 BinaryVector payload magic mismatch.");

            if (payload[4] != Version)
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorMalformed,
                    $"Unsupported Tier-3 BinaryVector version: {payload[4]}.");

            if (payload[5] != 0 || BinaryPrimitives.ReadUInt32BigEndian(payload[12..]) != 0)
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorMalformed,
                    "Tier-3 BinaryVector reserved fields must be zero.");

            var vectorCount = BinaryPrimitives.ReadUInt16BigEndian(payload[6..]);
            var metadataLength = BinaryPrimitives.ReadUInt32BigEndian(payload[8..]);
            if (metadataLength > payload.Length - PrefixSize)
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorTruncated,
                    "Tier-3 BinaryVector metadata length exceeds payload length.");

            var offset = PrefixSize;
            var metadataBytes = payload.Slice(offset, (int)metadataLength).ToArray();
            offset += (int)metadataLength;

            var metadata = DecodeMetadata(metadataBytes);
            var vectors = DecodeVectors(payload, ref offset, vectorCount);
            if (offset != payload.Length)
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorMalformed,
                    "Tier-3 BinaryVector payload has trailing bytes.");

            RestoreVectorSearchVector(metadata, vectors);

            var json = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOpts);
            return (IFrame)JsonSerializer.Deserialize(json, clrType, JsonOpts)!;
        }
        catch (NpsCodecException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw BinaryVectorError(
                NcpErrorCodes.BinaryVectorMalformed,
                $"Tier-3 BinaryVector decode failed for {type}.",
                ex);
        }
    }

    private static void ExtractVectorSearchVector(Dictionary<string, object?> metadata, List<float[]> vectors)
    {
        if (!metadata.TryGetValue(VectorSearchKey, out var vectorSearchObj) ||
            vectorSearchObj is not Dictionary<string, object?> vectorSearch ||
            !vectorSearch.TryGetValue(VectorKey, out var vectorObj) ||
            !TryReadFloatArray(vectorObj, out var vector))
        {
            return;
        }

        var index = vectors.Count;
        vectors.Add(vector);
        vectorSearch[VectorKey] = new Dictionary<string, object?>
        {
            [MarkerKey] = index,
            ["dtype"] = "float32",
            ["dim"] = vector.Length,
        };
    }

    private static void RestoreVectorSearchVector(Dictionary<string, object?> metadata, IReadOnlyList<float[]> vectors)
    {
        if (!metadata.TryGetValue(VectorSearchKey, out var vectorSearchObj) ||
            vectorSearchObj is not Dictionary<string, object?> vectorSearch ||
            !vectorSearch.TryGetValue(VectorKey, out var markerObj))
        {
            return;
        }

        var marker = markerObj as Dictionary<string, object?>
                     ?? throw BinaryVectorError(
                         NcpErrorCodes.BinaryVectorMalformed,
                         "Tier-3 BinaryVector marker must be an object.");

        if (!marker.TryGetValue(MarkerKey, out var indexObj) || !TryReadInt32(indexObj, out var index))
            throw BinaryVectorError(
                NcpErrorCodes.BinaryVectorMalformed,
                "Tier-3 BinaryVector marker missing vector index.");

        if (index < 0 || index >= vectors.Count)
            throw BinaryVectorError(
                NcpErrorCodes.BinaryVectorIndexInvalid,
                $"Tier-3 BinaryVector marker references vector {index}, but only {vectors.Count} vectors are present.");

        if (!marker.TryGetValue("dtype", out var dtypeObj) || dtypeObj as string != "float32")
            throw BinaryVectorError(
                NcpErrorCodes.BinaryVectorDtypeUnsupported,
                "Tier-3 BinaryVector v1 only supports dtype=float32.");

        if (!marker.TryGetValue("dim", out var dimObj) || !TryReadInt32(dimObj, out var dim) || dim != vectors[index].Length)
            throw BinaryVectorError(
                NcpErrorCodes.BinaryVectorDimMismatch,
                "Tier-3 BinaryVector marker dimension does not match vector segment.");

        vectorSearch[VectorKey] = vectors[index];
    }

    private static Dictionary<string, object?> DecodeMetadata(byte[] metadataBytes)
    {
        var raw = MessagePackSerializer.Deserialize<Dictionary<string, object?>>(metadataBytes, MsgPackOpts);
        return Normalize(raw) as Dictionary<string, object?>
               ?? throw BinaryVectorError(
                   NcpErrorCodes.BinaryVectorMalformed,
                   "Tier-3 BinaryVector metadata root must be a map.");
    }

    private static List<float[]> DecodeVectors(ReadOnlySpan<byte> payload, ref int offset, int vectorCount)
    {
        var vectors = new List<float[]>(vectorCount);
        for (var i = 0; i < vectorCount; i++)
        {
            if (payload.Length - offset < sizeof(uint))
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorTruncated,
                    "Tier-3 BinaryVector vector segment missing dimension.");

            var dim = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
            offset += sizeof(uint);

            if (dim > int.MaxValue / sizeof(float))
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorDimMismatch,
                    "Tier-3 BinaryVector dimension exceeds supported array size.");

            var byteLength = checked((int)dim * sizeof(float));
            if (payload.Length - offset < byteLength)
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorTruncated,
                    "Tier-3 BinaryVector vector segment is truncated.");

            var vector = new float[(int)dim];
            for (var j = 0; j < vector.Length; j++)
            {
                vector[j] = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(offset, sizeof(float)));
                offset += sizeof(float);
            }

            vectors.Add(vector);
        }

        return vectors;
    }

    private static object? ConvertElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(property => property.Name, property => ConvertElement(property.Value)),
        JsonValueKind.Array => element.EnumerateArray()
            .Select(ConvertElement)
            .ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var value) => value,
        JsonValueKind.Number when element.TryGetUInt64(out var value) => value,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw BinaryVectorError(
            NcpErrorCodes.BinaryVectorMalformed,
            $"Unsupported JSON value in Tier-3 metadata: {element.ValueKind}."),
    };

    private static object? Normalize(object? value)
    {
        switch (value)
        {
            case null:
            case string:
            case bool:
            case byte:
            case sbyte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
            case float:
            case double:
            case decimal:
                return value;

            case Dictionary<string, object?> dict:
                return dict.ToDictionary(pair => pair.Key, pair => Normalize(pair.Value));

            case IDictionary<object, object?> dict:
                return dict.ToDictionary(pair => pair.Key.ToString() ?? "", pair => Normalize(pair.Value));

            case object?[] array:
                return array.Select(Normalize).ToList();

            case IList<object?> list:
                return list.Select(Normalize).ToList();

            default:
                return value;
        }
    }

    private static bool TryReadFloatArray(object? value, out float[] vector)
    {
        switch (value)
        {
            case float[] values:
                vector = values;
                return true;

            case IReadOnlyList<float> values:
                vector = values.ToArray();
                return true;

            case IReadOnlyList<object?> values:
                vector = new float[values.Count];
                for (var i = 0; i < values.Count; i++)
                {
                    if (!TryReadSingle(values[i], out vector[i]))
                    {
                        vector = [];
                        return false;
                    }
                }
                return true;

            default:
                vector = [];
                return false;
        }
    }

    private static bool TryReadSingle(object? value, out float result)
    {
        switch (value)
        {
            case byte v: result = v; return true;
            case sbyte v: result = v; return true;
            case short v: result = v; return true;
            case ushort v: result = v; return true;
            case int v: result = v; return true;
            case uint v: result = v; return true;
            case long v: result = v; return true;
            case ulong v when v <= int.MaxValue: result = v; return true;
            case float v: result = v; return true;
            case double v when v is >= float.MinValue and <= float.MaxValue: result = (float)v; return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryReadInt32(object? value, out int result)
    {
        switch (value)
        {
            case byte v: result = v; return true;
            case sbyte v: result = v; return true;
            case short v: result = v; return true;
            case ushort v: result = v; return true;
            case int v: result = v; return true;
            case uint v when v <= int.MaxValue: result = (int)v; return true;
            case long v when v is >= int.MinValue and <= int.MaxValue: result = (int)v; return true;
            case ulong v when v <= int.MaxValue: result = (int)v; return true;
            default:
                result = 0;
                return false;
        }
    }

    private static NpsCodecException BinaryVectorError(string errorCode, string message) =>
        new(message, NpsStatusCodes.ClientBadFrame, errorCode);

    private static NpsCodecException BinaryVectorError(string errorCode, string message, Exception inner) =>
        new(message, inner, NpsStatusCodes.ClientBadFrame, errorCode);
}
