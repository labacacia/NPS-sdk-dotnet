// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

namespace NPS.Core.Serialization;

[GeneratedMessagePackResolver]
internal partial class NpsCoreMessagePackResolver;

/// <summary>
/// NativeAOT-safe MessagePack resolver for NPS core frames and shared JSON value types.
/// Upper-layer protocol packages compose their generated formatters with this resolver.
/// </summary>
public static class NpsMessagePackResolver
{
    private static readonly JsonElementMessagePackFormatter JsonElementFormatter = new();

    public static IFormatterResolver Instance { get; } = CompositeResolver.Create(
        new IMessagePackFormatter[]
        {
            JsonElementFormatter,
            new StaticNullableFormatter<JsonElement>(JsonElementFormatter),
            NullableStringFormatter.Instance,
            BooleanFormatter.Instance,
            NullableBooleanFormatter.Instance,
            UInt32Formatter.Instance,
            NullableUInt32Formatter.Instance,
            UInt64Formatter.Instance,
            NullableUInt64Formatter.Instance,
            ByteArrayFormatter.Instance,
            new InterfaceReadOnlyListFormatter<string>(),
            new InterfaceReadOnlyListFormatter<JsonElement>(),
            new InterfaceReadOnlyListFormatter<SchemaField>(),
            new InterfaceReadOnlyListFormatter<JsonPatchOperation>(),
        },
        new IFormatterResolver[]
        {
            NpsCoreMessagePackResolver.Instance,
        });
}

internal sealed class JsonElementMessagePackFormatter : IMessagePackFormatter<JsonElement>
{
    public void Serialize(
        ref MessagePackWriter writer,
        JsonElement value,
        MessagePackSerializerOptions options)
    {
        WriteElement(ref writer, value);
    }

    public JsonElement Deserialize(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var jsonWriter = new Utf8JsonWriter(buffer))
        {
            WriteJson(ref reader, jsonWriter, options);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteElement(ref MessagePackWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteMapHeader(value.GetPropertyCount());
                foreach (var property in value.EnumerateObject())
                {
                    writer.Write(property.Name);
                    WriteElement(ref writer, property.Value);
                }
                break;

            case JsonValueKind.Array:
                writer.WriteArrayHeader(value.GetArrayLength());
                foreach (var item in value.EnumerateArray())
                    WriteElement(ref writer, item);
                break;

            case JsonValueKind.String:
                writer.Write(value.GetString());
                break;

            case JsonValueKind.Number when value.TryGetInt64(out var signed):
                writer.Write(signed);
                break;

            case JsonValueKind.Number when value.TryGetUInt64(out var unsigned):
                writer.Write(unsigned);
                break;

            case JsonValueKind.Number:
                writer.Write(value.GetDouble());
                break;

            case JsonValueKind.True:
                writer.Write(true);
                break;

            case JsonValueKind.False:
                writer.Write(false);
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNil();
                break;

            default:
                throw new MessagePackSerializationException(
                    $"Unsupported JsonElement kind: {value.ValueKind}.");
        }
    }

    private static void WriteJson(
        ref MessagePackReader reader,
        Utf8JsonWriter writer,
        MessagePackSerializerOptions options)
    {
        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Map:
                options.Security.DepthStep(ref reader);
                try
                {
                    var count = reader.ReadMapHeader();
                    writer.WriteStartObject();
                    for (var i = 0; i < count; i++)
                    {
                        writer.WritePropertyName(
                            reader.ReadString()
                            ?? throw new MessagePackSerializationException(
                                "JsonElement map keys must be strings."));
                        WriteJson(ref reader, writer, options);
                    }
                    writer.WriteEndObject();
                }
                finally
                {
                    reader.Depth--;
                }
                break;

            case MessagePackType.Array:
                options.Security.DepthStep(ref reader);
                try
                {
                    var count = reader.ReadArrayHeader();
                    writer.WriteStartArray();
                    for (var i = 0; i < count; i++)
                        WriteJson(ref reader, writer, options);
                    writer.WriteEndArray();
                }
                finally
                {
                    reader.Depth--;
                }
                break;

            case MessagePackType.String:
                writer.WriteStringValue(reader.ReadString());
                break;

            case MessagePackType.Binary:
                writer.WriteBase64StringValue(reader.ReadBytes()?.ToArray() ?? []);
                break;

            case MessagePackType.Integer:
                if (reader.NextCode >= MessagePackCode.MinNegativeFixInt ||
                    reader.NextCode is >= MessagePackCode.Int8 and <= MessagePackCode.Int64)
                {
                    writer.WriteNumberValue(reader.ReadInt64());
                }
                else
                {
                    writer.WriteNumberValue(reader.ReadUInt64());
                }
                break;

            case MessagePackType.Float when reader.NextCode == MessagePackCode.Float32:
                writer.WriteNumberValue(reader.ReadSingle());
                break;

            case MessagePackType.Float:
                writer.WriteNumberValue(reader.ReadDouble());
                break;

            case MessagePackType.Boolean:
                writer.WriteBooleanValue(reader.ReadBoolean());
                break;

            case MessagePackType.Nil:
                reader.ReadNil();
                writer.WriteNullValue();
                break;

            default:
                throw new MessagePackSerializationException(
                    $"MessagePack type {reader.NextMessagePackType} cannot be represented as JsonElement.");
        }
    }
}
