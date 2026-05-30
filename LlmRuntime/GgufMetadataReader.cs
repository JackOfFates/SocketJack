using System.Text;

namespace LlmRuntime;

public sealed class GgufMetadataReader
{
    private const uint GgufMagic = 0x46554747;

    public Dictionary<string, object?> Metadata { get; } = new(StringComparer.Ordinal);

    public static Task<GgufMetadataReader?> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Read(filePath), cancellationToken);
    }

    public static GgufMetadataReader? Read(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            uint magic = reader.ReadUInt32();
            if (magic != GgufMagic)
                return null;

            uint version = reader.ReadUInt32();
            if (version < 2 || version > 3)
                return null;

            _ = reader.ReadUInt64();
            ulong metadataKvCount = reader.ReadUInt64();

            var result = new GgufMetadataReader();
            for (ulong i = 0; i < metadataKvCount; i++)
            {
                string key = ReadGgufString(reader);
                uint valueType = reader.ReadUInt32();
                object? value = ReadGgufValue(reader, valueType);
                if (value != null)
                    result.Metadata[key] = value;
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    public string? GetString(string key) =>
        Metadata.TryGetValue(key, out var value) && value is string text ? text : null;

    public ulong? GetUInt64(string key) =>
        Metadata.TryGetValue(key, out var value) ? ConvertToUInt64(value) : null;

    public uint? GetUInt32(string key) =>
        Metadata.TryGetValue(key, out var value) ? ConvertToUInt32(value) : null;

    private static ulong? ConvertToUInt64(object? value) => value switch
    {
        ulong unsigned => unsigned,
        long signed when signed >= 0 => (ulong)signed,
        uint unsigned => unsigned,
        int signed when signed >= 0 => (ulong)signed,
        ushort unsigned => unsigned,
        short signed when signed >= 0 => (ulong)signed,
        byte unsigned => unsigned,
        sbyte signed when signed >= 0 => (ulong)signed,
        _ => null
    };

    private static uint? ConvertToUInt32(object? value) => value switch
    {
        uint unsigned => unsigned,
        int signed when signed >= 0 => (uint)signed,
        ulong unsigned when unsigned <= uint.MaxValue => (uint)unsigned,
        long signed when signed >= 0 && signed <= uint.MaxValue => (uint)signed,
        ushort unsigned => unsigned,
        short signed when signed >= 0 => (uint)signed,
        byte unsigned => unsigned,
        sbyte signed when signed >= 0 => (uint)signed,
        _ => null
    };

    private static string ReadGgufString(BinaryReader reader)
    {
        ulong len = reader.ReadUInt64();
        if (len > 1024 * 1024)
            throw new InvalidDataException("GGUF string is too large.");

        byte[] bytes = reader.ReadBytes((int)len);
        if ((ulong)bytes.Length != len)
            throw new EndOfStreamException();

        return Encoding.UTF8.GetString(bytes);
    }

    private const uint GgufTypeUint8 = 0;
    private const uint GgufTypeInt8 = 1;
    private const uint GgufTypeUint16 = 2;
    private const uint GgufTypeInt16 = 3;
    private const uint GgufTypeUint32 = 4;
    private const uint GgufTypeInt32 = 5;
    private const uint GgufTypeFloat32 = 6;
    private const uint GgufTypeBool = 7;
    private const uint GgufTypeString = 8;
    private const uint GgufTypeArray = 9;
    private const uint GgufTypeUint64 = 10;
    private const uint GgufTypeInt64 = 11;
    private const uint GgufTypeFloat64 = 12;

    private static object? ReadGgufValue(BinaryReader reader, uint valueType)
    {
        return valueType switch
        {
            GgufTypeUint8 => reader.ReadByte(),
            GgufTypeInt8 => reader.ReadSByte(),
            GgufTypeUint16 => reader.ReadUInt16(),
            GgufTypeInt16 => reader.ReadInt16(),
            GgufTypeUint32 => reader.ReadUInt32(),
            GgufTypeInt32 => reader.ReadInt32(),
            GgufTypeFloat32 => reader.ReadSingle(),
            GgufTypeBool => reader.ReadByte() != 0,
            GgufTypeString => ReadGgufString(reader),
            GgufTypeUint64 => reader.ReadUInt64(),
            GgufTypeInt64 => reader.ReadInt64(),
            GgufTypeFloat64 => reader.ReadDouble(),
            GgufTypeArray => ReadGgufArray(reader),
            _ => throw new InvalidDataException($"Unknown GGUF value type: {valueType}")
        };
    }

    private static object? ReadGgufArray(BinaryReader reader)
    {
        uint arrayType = reader.ReadUInt32();
        ulong length = reader.ReadUInt64();
        if (length > 10000)
        {
            for (ulong i = 0; i < length; i++)
                SkipGgufValue(reader, arrayType);
            return null;
        }

        var values = new List<object?>();
        for (ulong i = 0; i < length; i++)
            values.Add(ReadGgufValue(reader, arrayType));
        return values;
    }

    private static void SkipGgufValue(BinaryReader reader, uint valueType)
    {
        switch (valueType)
        {
            case GgufTypeUint8:
            case GgufTypeInt8:
            case GgufTypeBool:
                reader.ReadByte();
                break;
            case GgufTypeUint16:
            case GgufTypeInt16:
                reader.ReadUInt16();
                break;
            case GgufTypeUint32:
            case GgufTypeInt32:
            case GgufTypeFloat32:
                reader.ReadUInt32();
                break;
            case GgufTypeUint64:
            case GgufTypeInt64:
            case GgufTypeFloat64:
                reader.ReadUInt64();
                break;
            case GgufTypeString:
                _ = ReadGgufString(reader);
                break;
            case GgufTypeArray:
                uint arrayType = reader.ReadUInt32();
                ulong length = reader.ReadUInt64();
                for (ulong i = 0; i < length; i++)
                    SkipGgufValue(reader, arrayType);
                break;
            default:
                throw new InvalidDataException($"Unknown GGUF value type: {valueType}");
        }
    }
}
