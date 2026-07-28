// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
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
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    [RequiresDynamicCode("Use Encode(IFrame, FrameRegistry) with source-generated frame metadata for NativeAOT.")]
    [RequiresUnreferencedCode("Use Encode(IFrame, FrameRegistry) with source-generated frame metadata when trimming.")]
    public byte[] Encode(IFrame frame)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(frame, frame.GetType(), JsonOpts);
            return EncodePayload(json);
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

    public byte[] Encode(IFrame frame, FrameRegistry registry)
    {
        try
        {
            var json = registry.ResolveRegistration(frame).JsonEncoder(frame);
            return EncodePayload(json);
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

    private static byte[] EncodePayload(byte[] json)
    {
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

        var metadataBytes = EncodeMetadata(metadata);
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

    public IFrame Decode(FrameType type, ReadOnlySpan<byte> payload, FrameRegistry registry)
    {
        var registration = registry.ResolveRegistration(type);
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

            var json = EncodeMetadataJson(metadata);
            return registration.JsonDecoder(json);
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
        var sequence = new ReadOnlySequence<byte>(metadataBytes);
        var reader = new MessagePackReader(sequence);
        var metadata = ReadMetadataValue(ref reader) as Dictionary<string, object?>
                       ?? throw BinaryVectorError(
                           NcpErrorCodes.BinaryVectorMalformed,
                           "Tier-3 BinaryVector metadata root must be a map.");

        if (!reader.End)
            throw BinaryVectorError(
                NcpErrorCodes.BinaryVectorMalformed,
                "Tier-3 BinaryVector metadata has trailing MessagePack values.");

        return metadata;
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

    private static byte[] EncodeMetadata(Dictionary<string, object?> metadata)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        WriteMetadataValue(ref writer, metadata);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] EncodeMetadataJson(Dictionary<string, object?> metadata)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteMetadataJson(writer, metadata);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMetadataJson(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;

            case string text:
                writer.WriteStringValue(text);
                break;

            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;

            case byte number:
                writer.WriteNumberValue(number);
                break;

            case sbyte number:
                writer.WriteNumberValue(number);
                break;

            case short number:
                writer.WriteNumberValue(number);
                break;

            case ushort number:
                writer.WriteNumberValue(number);
                break;

            case int number:
                writer.WriteNumberValue(number);
                break;

            case uint number:
                writer.WriteNumberValue(number);
                break;

            case long number:
                writer.WriteNumberValue(number);
                break;

            case ulong number:
                writer.WriteNumberValue(number);
                break;

            case float number:
                writer.WriteNumberValue(number);
                break;

            case double number:
                writer.WriteNumberValue(number);
                break;

            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;

            case float[] vector:
                writer.WriteStartArray();
                foreach (var number in vector)
                    writer.WriteNumberValue(number);
                writer.WriteEndArray();
                break;

            case Dictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var pair in map)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteMetadataJson(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;

            case IList<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                    WriteMetadataJson(writer, item);
                writer.WriteEndArray();
                break;

            default:
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorMalformed,
                    $"Unsupported Tier-3 metadata value type: {value.GetType().FullName}.");
        }
    }

    private static void WriteMetadataValue(ref MessagePackWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNil();
                break;

            case string:
                writer.Write((string)value);
                break;

            case bool:
                writer.Write((bool)value);
                break;

            case byte:
                writer.Write((byte)value);
                break;

            case sbyte:
                writer.Write((sbyte)value);
                break;

            case short:
                writer.Write((short)value);
                break;

            case ushort:
                writer.Write((ushort)value);
                break;

            case int:
                writer.Write((int)value);
                break;

            case uint:
                writer.Write((uint)value);
                break;

            case long:
                writer.Write((long)value);
                break;

            case ulong:
                writer.Write((ulong)value);
                break;

            case float:
                writer.Write((float)value);
                break;

            case double:
                writer.Write((double)value);
                break;

            case Dictionary<string, object?> dict:
                writer.WriteMapHeader(dict.Count);
                foreach (var pair in dict)
                {
                    writer.Write(pair.Key);
                    WriteMetadataValue(ref writer, pair.Value);
                }
                break;

            case IList<object?> list:
                writer.WriteArrayHeader(list.Count);
                foreach (var item in list)
                    WriteMetadataValue(ref writer, item);
                break;

            default:
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorMalformed,
                    $"Unsupported Tier-3 metadata value type: {value.GetType().FullName}.");
        }
    }

    private static object? ReadMetadataValue(ref MessagePackReader reader)
    {
        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Map:
                {
                    MessagePackSecurity.UntrustedData.DepthStep(ref reader);
                    try
                    {
                        var count = reader.ReadMapHeader();
                        var map = new Dictionary<string, object?>(count, StringComparer.Ordinal);
                        for (var i = 0; i < count; i++)
                        {
                            var key = reader.ReadString()
                                      ?? throw BinaryVectorError(
                                          NcpErrorCodes.BinaryVectorMalformed,
                                          "Tier-3 BinaryVector metadata map keys must be strings.");
                            map[key] = ReadMetadataValue(ref reader);
                        }
                        return map;
                    }
                    finally
                    {
                        reader.Depth--;
                    }
                }

            case MessagePackType.Array:
                {
                    MessagePackSecurity.UntrustedData.DepthStep(ref reader);
                    try
                    {
                        var count = reader.ReadArrayHeader();
                        var list = new List<object?>(count);
                        for (var i = 0; i < count; i++)
                            list.Add(ReadMetadataValue(ref reader));
                        return list;
                    }
                    finally
                    {
                        reader.Depth--;
                    }
                }

            case MessagePackType.String:
                return reader.ReadString();

            case MessagePackType.Binary:
                return reader.ReadBytes()?.ToArray();

            case MessagePackType.Integer:
                return reader.NextCode >= MessagePackCode.MinNegativeFixInt ||
                       reader.NextCode is >= MessagePackCode.Int8 and <= MessagePackCode.Int64
                    ? reader.ReadInt64()
                    : reader.ReadUInt64();

            case MessagePackType.Float when reader.NextCode == MessagePackCode.Float32:
                return reader.ReadSingle();

            case MessagePackType.Float:
                return reader.ReadDouble();

            case MessagePackType.Boolean:
                return reader.ReadBoolean();

            case MessagePackType.Nil:
                reader.ReadNil();
                return null;

            default:
                throw BinaryVectorError(
                    NcpErrorCodes.BinaryVectorMalformed,
                    $"Unsupported MessagePack type in Tier-3 metadata: {reader.NextMessagePackType}.");
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
