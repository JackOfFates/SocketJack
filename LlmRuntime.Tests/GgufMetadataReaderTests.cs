using System.Text;
using LlmRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class GgufMetadataReaderTests
{
    [TestMethod]
    public void Read_ParsesSyntheticGgufMetadata()
    {
        string path = Path.Combine(Path.GetTempPath(), "llmruntime_test_" + Guid.NewGuid().ToString("N") + ".gguf");
        try
        {
            WriteSyntheticGguf(path);

            var reader = GgufMetadataReader.Read(path);

            Assert.IsNotNull(reader);
            Assert.AreEqual("llama", reader.GetString("general.architecture"));
            Assert.AreEqual((uint)4096, reader.GetUInt32("llama.context_length"));
            Assert.AreEqual((ulong)7_000_000_000, reader.GetUInt64("general.parameter_count"));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void Read_ReturnsNullForInvalidFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "llmruntime_invalid_" + Guid.NewGuid().ToString("N") + ".gguf");
        try
        {
            File.WriteAllText(path, "not a gguf");
            Assert.IsNull(GgufMetadataReader.Read(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    internal static void WriteSyntheticGguf(string path, string architecture = "llama", string? generalType = null)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(fs, Encoding.UTF8);
        writer.Write(0x46554747u);
        writer.Write(3u);
        writer.Write(0UL);
        writer.Write(generalType == null ? 3UL : 4UL);

        WriteStringValue(writer, "general.architecture", architecture);
        if (generalType != null)
            WriteStringValue(writer, "general.type", generalType);
        WriteUInt32Value(writer, architecture + ".context_length", 4096);
        WriteUInt64Value(writer, "general.parameter_count", 7_000_000_000);
    }

    internal static void WriteSyntheticGgufWithoutMetadata(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(fs, Encoding.UTF8);
        writer.Write(0x46554747u);
        writer.Write(3u);
        writer.Write(0UL);
        writer.Write(0UL);
    }

    private static void WriteStringValue(BinaryWriter writer, string key, string value)
    {
        WriteGgufString(writer, key);
        writer.Write(8u);
        WriteGgufString(writer, value);
    }

    private static void WriteUInt32Value(BinaryWriter writer, string key, uint value)
    {
        WriteGgufString(writer, key);
        writer.Write(4u);
        writer.Write(value);
    }

    private static void WriteUInt64Value(BinaryWriter writer, string key, ulong value)
    {
        WriteGgufString(writer, key);
        writer.Write(10u);
        writer.Write(value);
    }

    private static void WriteGgufString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
