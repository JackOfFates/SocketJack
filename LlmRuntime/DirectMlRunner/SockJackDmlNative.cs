using System.Runtime.InteropServices;
using System.Text;

namespace LlmRuntime.DirectMlRunner;

internal sealed class SockJackDmlProbe
{
    public bool LibraryFound { get; init; }
    public bool NativeDirectMlAvailable { get; init; }
    public uint Status { get; init; }
    public string LibraryPath { get; init; } = "";
    public string AdapterName { get; init; } = "";
    public ulong DedicatedVideoMemory { get; init; }
    public ulong SharedSystemMemory { get; init; }
    public string Message { get; init; } = "";
}

internal sealed class SockJackDmlIdentitySmokeResult
{
    public bool LibraryFound { get; init; }
    public bool Success { get; init; }
    public uint Status { get; init; }
    public string LibraryPath { get; init; } = "";
    public IReadOnlyList<float> Input { get; init; } = [];
    public IReadOnlyList<float> Output { get; init; } = [];
    public string Message { get; init; } = "";
}

internal sealed class SockJackDmlModelLoadResult
{
    public bool LibraryFound { get; init; }
    public bool Success { get; init; }
    public uint Status { get; init; }
    public string LibraryPath { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public ulong FileSize { get; init; }
    public ulong TensorCount { get; init; }
    public ulong MetadataCount { get; init; }
    public ulong DataOffset { get; init; }
    public uint ContextLength { get; init; }
    public uint EmbeddingLength { get; init; }
    public uint SupportedTensorCount { get; init; }
    public uint UnsupportedTensorCount { get; init; }
    public bool DirectMlDeviceReady { get; init; }
    public string Architecture { get; init; } = "";
    public string ModelName { get; init; } = "";
    public string Message { get; init; } = "";
}

internal sealed class SockJackDmlTensorSmokeResult
{
    public bool LibraryFound { get; init; }
    public bool Success { get; init; }
    public uint Status { get; init; }
    public string LibraryPath { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string TensorName { get; init; } = "";
    public ulong TensorElementCount { get; init; }
    public ulong DispatchedElementCount { get; init; }
    public ulong TensorByteCount { get; init; }
    public uint TensorType { get; init; }
    public IReadOnlyList<ulong> Dimensions { get; init; } = [];
    public IReadOnlyList<float> InputPreview { get; init; } = [];
    public IReadOnlyList<float> OutputPreview { get; init; } = [];
    public string Message { get; init; } = "";
}

internal sealed class SockJackDmlLoadedModel : IDisposable
{
    private nint _libraryHandle;
    private nint _modelHandle;
    private readonly SockJackDmlNative.SockJackDmlUnloadModelDelegate _unload;

    public SockJackDmlLoadedModel(nint libraryHandle, nint modelHandle, SockJackDmlNative.SockJackDmlUnloadModelDelegate unload, SockJackDmlModelLoadResult info)
    {
        _libraryHandle = libraryHandle;
        _modelHandle = modelHandle;
        _unload = unload;
        Info = info;
    }

    public SockJackDmlModelLoadResult Info { get; }

    public void Dispose()
    {
        if (_modelHandle != 0)
        {
            _unload(_modelHandle);
            _modelHandle = 0;
        }

        if (_libraryHandle != 0)
        {
            NativeLibrary.Free(_libraryHandle);
            _libraryHandle = 0;
        }
    }
}

internal static class SockJackDmlNative
{
    private const string LibraryName = "SockJackDml.dll";

    public static SockJackDmlProbe Probe()
    {
        string libraryPath = ResolveLibraryPath();
        if (string.IsNullOrWhiteSpace(libraryPath) || !NativeLibrary.TryLoad(libraryPath, out nint handle))
        {
            return new SockJackDmlProbe
            {
                LibraryFound = false,
                Message = "SockJackDml.dll was not found. Run tools\\Build-SockJackDml.ps1."
            };
        }

        try
        {
            const int resultSize = 800;
            const int nativeAvailableOffset = 8;
            const int dedicatedVideoMemoryOffset = 16;
            const int sharedSystemMemoryOffset = 24;
            const int adapterNameOffset = 32;
            const int adapterNameCharacterCount = 128;
            const int messageOffset = 288;
            const int messageLength = 512;

            nint export = NativeLibrary.GetExport(handle, "SockJackDmlProbe");
            var probe = Marshal.GetDelegateForFunctionPointer<SockJackDmlProbeDelegate>(export);
            nint result = Marshal.AllocHGlobal(resultSize);
            try
            {
                Span<byte> zero = stackalloc byte[resultSize];
                Marshal.Copy(zero.ToArray(), 0, result, resultSize);
                Marshal.WriteInt32(result, resultSize);
                uint status = probe(result);

                byte[] message = new byte[messageLength];
                Marshal.Copy(result + messageOffset, message, 0, message.Length);
                return new SockJackDmlProbe
                {
                    LibraryFound = true,
                    NativeDirectMlAvailable = status == 0 && Marshal.ReadInt32(result, nativeAvailableOffset) != 0,
                    Status = status,
                    LibraryPath = libraryPath,
                    AdapterName = DecodeFixedUnicodeString(result + adapterNameOffset, adapterNameCharacterCount),
                    DedicatedVideoMemory = (ulong)Marshal.ReadInt64(result, dedicatedVideoMemoryOffset),
                    SharedSystemMemory = (ulong)Marshal.ReadInt64(result, sharedSystemMemoryOffset),
                    Message = DecodeMessage(message)
                };
            }
            finally
            {
                Marshal.FreeHGlobal(result);
            }
        }
        catch (Exception ex)
        {
            return new SockJackDmlProbe
            {
                LibraryFound = true,
                NativeDirectMlAvailable = false,
                Status = 100,
                LibraryPath = libraryPath,
                Message = ex.Message
            };
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    public static SockJackDmlIdentitySmokeResult RunIdentitySmoke()
    {
        string libraryPath = ResolveLibraryPath();
        if (string.IsNullOrWhiteSpace(libraryPath) || !NativeLibrary.TryLoad(libraryPath, out nint handle))
        {
            return new SockJackDmlIdentitySmokeResult
            {
                LibraryFound = false,
                Message = "SockJackDml.dll was not found. Run tools\\Build-SockJackDml.ps1."
            };
        }

        try
        {
            nint export = NativeLibrary.GetExport(handle, "SockJackDmlRunIdentityFloat32");
            var identity = Marshal.GetDelegateForFunctionPointer<SockJackDmlRunIdentityFloat32Delegate>(export);
            float[] input = [1.25f, -2.5f, 3.75f, 0f, 42f, -99.5f, 6.125f, 8f];
            float[] output = new float[input.Length];
            GCHandle inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            GCHandle outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);
            try
            {
                uint status = identity(inputHandle.AddrOfPinnedObject(), outputHandle.AddrOfPinnedObject(), (uint)input.Length);
                bool matches = status == 0 && input.SequenceEqual(output);
                return new SockJackDmlIdentitySmokeResult
                {
                    LibraryFound = true,
                    Success = matches,
                    Status = status,
                    LibraryPath = libraryPath,
                    Input = input,
                    Output = output,
                    Message = matches ? "DirectML identity dispatch returned the expected tensor." : "DirectML identity dispatch failed or returned unexpected output."
                };
            }
            finally
            {
                inputHandle.Free();
                outputHandle.Free();
            }
        }
        catch (Exception ex)
        {
            return new SockJackDmlIdentitySmokeResult
            {
                LibraryFound = true,
                Success = false,
                Status = 100,
                LibraryPath = libraryPath,
                Message = ex.Message
            };
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    public static SockJackDmlLoadedModel LoadModel(string modelPath, uint contextLength, int gpuLayerCount)
    {
        string libraryPath = ResolveLibraryPath();
        if (string.IsNullOrWhiteSpace(libraryPath) || !NativeLibrary.TryLoad(libraryPath, out nint handle))
            throw new FileNotFoundException("SockJackDml.dll was not found. Run tools\\Build-SockJackDml.ps1.", LibraryName);

        try
        {
            nint loadExport = NativeLibrary.GetExport(handle, "SockJackDmlLoadModel");
            nint unloadExport = NativeLibrary.GetExport(handle, "SockJackDmlUnloadModel");
            var load = Marshal.GetDelegateForFunctionPointer<SockJackDmlLoadModelDelegate>(loadExport);
            var unload = Marshal.GetDelegateForFunctionPointer<SockJackDmlUnloadModelDelegate>(unloadExport);
            var options = new NativeSockJackDmlModelLoadOptions
            {
                Size = (uint)Marshal.SizeOf<NativeSockJackDmlModelLoadOptions>(),
                ContextLength = contextLength,
                GpuLayerCount = gpuLayerCount
            };
            var info = new NativeSockJackDmlModelInfo
            {
                Size = (uint)Marshal.SizeOf<NativeSockJackDmlModelInfo>()
            };

            uint status = load(modelPath, ref options, ref info, out nint modelHandle);
            SockJackDmlModelLoadResult result = ConvertModelLoadResult(libraryPath, modelPath, status, info);
            if (status != 0 || modelHandle == 0)
                throw new InvalidOperationException(result.Message);

            return new SockJackDmlLoadedModel(handle, modelHandle, unload, result);
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }

    public static SockJackDmlModelLoadResult RunModelLoadSmoke(string modelPath, uint contextLength, int gpuLayerCount)
    {
        string libraryPath = ResolveLibraryPath();
        if (string.IsNullOrWhiteSpace(libraryPath) || !NativeLibrary.TryLoad(libraryPath, out nint handle))
        {
            return new SockJackDmlModelLoadResult
            {
                LibraryFound = false,
                ModelPath = modelPath,
                Message = "SockJackDml.dll was not found. Run tools\\Build-SockJackDml.ps1."
            };
        }

        try
        {
            nint loadExport = NativeLibrary.GetExport(handle, "SockJackDmlLoadModel");
            nint unloadExport = NativeLibrary.GetExport(handle, "SockJackDmlUnloadModel");
            var load = Marshal.GetDelegateForFunctionPointer<SockJackDmlLoadModelDelegate>(loadExport);
            var unload = Marshal.GetDelegateForFunctionPointer<SockJackDmlUnloadModelDelegate>(unloadExport);
            var options = new NativeSockJackDmlModelLoadOptions
            {
                Size = (uint)Marshal.SizeOf<NativeSockJackDmlModelLoadOptions>(),
                ContextLength = contextLength,
                GpuLayerCount = gpuLayerCount
            };
            var info = new NativeSockJackDmlModelInfo
            {
                Size = (uint)Marshal.SizeOf<NativeSockJackDmlModelInfo>()
            };

            uint status = load(modelPath, ref options, ref info, out nint modelHandle);
            if (modelHandle != 0)
                unload(modelHandle);
            return ConvertModelLoadResult(libraryPath, modelPath, status, info);
        }
        catch (Exception ex)
        {
            return new SockJackDmlModelLoadResult
            {
                LibraryFound = true,
                Success = false,
                Status = 100,
                LibraryPath = libraryPath,
                ModelPath = modelPath,
                Message = ex.Message
            };
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    public static SockJackDmlTensorSmokeResult RunTensorIdentitySmoke(string modelPath, uint contextLength, int gpuLayerCount, string? tensorName = null, uint maxElementCount = 1024)
    {
        string libraryPath = ResolveLibraryPath();
        if (string.IsNullOrWhiteSpace(libraryPath) || !NativeLibrary.TryLoad(libraryPath, out nint handle))
        {
            return new SockJackDmlTensorSmokeResult
            {
                LibraryFound = false,
                ModelPath = modelPath,
                Message = "SockJackDml.dll was not found. Run tools\\Build-SockJackDml.ps1."
            };
        }

        nint modelHandle = 0;
        try
        {
            nint loadExport = NativeLibrary.GetExport(handle, "SockJackDmlLoadModel");
            nint tensorExport = NativeLibrary.GetExport(handle, "SockJackDmlRunTensorIdentityFloat32");
            var load = Marshal.GetDelegateForFunctionPointer<SockJackDmlLoadModelDelegate>(loadExport);
            var tensorSmoke = Marshal.GetDelegateForFunctionPointer<SockJackDmlRunTensorIdentityFloat32Delegate>(tensorExport);

            var loadOptions = new NativeSockJackDmlModelLoadOptions
            {
                Size = (uint)Marshal.SizeOf<NativeSockJackDmlModelLoadOptions>(),
                ContextLength = contextLength,
                GpuLayerCount = gpuLayerCount
            };
            var loadInfo = new NativeSockJackDmlModelInfo
            {
                Size = (uint)Marshal.SizeOf<NativeSockJackDmlModelInfo>()
            };

            uint loadStatus = load(modelPath, ref loadOptions, ref loadInfo, out modelHandle);
            if (loadStatus != 0 || modelHandle == 0)
            {
                SockJackDmlModelLoadResult loadResult = ConvertModelLoadResult(libraryPath, modelPath, loadStatus, loadInfo);
                return new SockJackDmlTensorSmokeResult
                {
                    LibraryFound = true,
                    Success = false,
                    Status = loadResult.Status,
                    LibraryPath = libraryPath,
                    ModelPath = modelPath,
                    Message = loadResult.Message
                };
            }

            var options = new NativeSockJackDmlTensorDispatchOptions
            {
                Size = (uint)Marshal.SizeOf<NativeSockJackDmlTensorDispatchOptions>(),
                MaxElementCount = maxElementCount,
                TensorName = tensorName ?? ""
            };
            var result = new NativeSockJackDmlTensorDispatchResult
            {
                Size = (uint)Marshal.SizeOf<NativeSockJackDmlTensorDispatchResult>(),
                Dimensions = new ulong[8],
                InputPreview = new float[8],
                OutputPreview = new float[8]
            };

            uint status = tensorSmoke(modelHandle, ref options, ref result);
            return ConvertTensorSmokeResult(libraryPath, modelPath, status, result);
        }
        catch (Exception ex)
        {
            return new SockJackDmlTensorSmokeResult
            {
                LibraryFound = true,
                Success = false,
                Status = 100,
                LibraryPath = libraryPath,
                ModelPath = modelPath,
                Message = ex.Message
            };
        }
        finally
        {
            if (modelHandle != 0)
            {
                try
                {
                    nint unloadExport = NativeLibrary.GetExport(handle, "SockJackDmlUnloadModel");
                    var unload = Marshal.GetDelegateForFunctionPointer<SockJackDmlUnloadModelDelegate>(unloadExport);
                    unload(modelHandle);
                }
                catch
                {
                    // Keep cleanup best-effort; the smoke result should report the original failure.
                }
            }

            NativeLibrary.Free(handle);
        }
    }

    private static string ResolveLibraryPath()
    {
        foreach (string candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "";
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, LibraryName);
        yield return Path.Combine(Environment.CurrentDirectory, "Tools", "DirectML", LibraryName);
        yield return Path.Combine(Environment.CurrentDirectory, LibraryName);
    }

    private static string DecodeMessage(byte[] message)
    {
        int length = Array.IndexOf(message, (byte)0);
        if (length < 0)
            length = message.Length;
        return Encoding.UTF8.GetString(message, 0, length);
    }

    private static string DecodeFixedUnicodeString(nint value, int characterCount)
    {
        string text = Marshal.PtrToStringUni(value, characterCount) ?? "";
        int terminator = text.IndexOf('\0');
        return terminator >= 0 ? text[..terminator] : text.TrimEnd('\0');
    }

    private static SockJackDmlModelLoadResult ConvertModelLoadResult(string libraryPath, string modelPath, uint status, NativeSockJackDmlModelInfo info)
    {
        return new SockJackDmlModelLoadResult
        {
            LibraryFound = true,
            Success = status == 0 && info.Status == 0,
            Status = status == 0 ? info.Status : status,
            LibraryPath = libraryPath,
            ModelPath = modelPath,
            FileSize = info.FileSize,
            TensorCount = info.TensorCount,
            MetadataCount = info.MetadataCount,
            DataOffset = info.DataOffset,
            ContextLength = info.ContextLength,
            EmbeddingLength = info.EmbeddingLength,
            SupportedTensorCount = info.SupportedTensorCount,
            UnsupportedTensorCount = info.UnsupportedTensorCount,
            DirectMlDeviceReady = info.DirectMlDeviceReady != 0,
            Architecture = info.Architecture ?? "",
            ModelName = info.ModelName ?? "",
            Message = info.Message ?? ""
        };
    }

    private static SockJackDmlTensorSmokeResult ConvertTensorSmokeResult(string libraryPath, string modelPath, uint status, NativeSockJackDmlTensorDispatchResult info)
    {
        uint previewCount = Math.Min(info.PreviewCount, 8);
        uint dimensionCount = Math.Min(info.DimensionCount, 8);
        return new SockJackDmlTensorSmokeResult
        {
            LibraryFound = true,
            Success = status == 0 && info.Status == 0,
            Status = status == 0 ? info.Status : status,
            LibraryPath = libraryPath,
            ModelPath = modelPath,
            TensorName = info.TensorName ?? "",
            TensorElementCount = info.TensorElementCount,
            DispatchedElementCount = info.DispatchedElementCount,
            TensorByteCount = info.TensorByteCount,
            TensorType = info.TensorType,
            Dimensions = (info.Dimensions ?? []).Take((int)dimensionCount).ToArray(),
            InputPreview = (info.InputPreview ?? []).Take((int)previewCount).ToArray(),
            OutputPreview = (info.OutputPreview ?? []).Take((int)previewCount).ToArray(),
            Message = info.Message ?? ""
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSockJackDmlModelLoadOptions
    {
        public uint Size;
        public uint ContextLength;
        public int GpuLayerCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeSockJackDmlModelInfo
    {
        public uint Size;
        public uint Status;
        public ulong FileSize;
        public ulong TensorCount;
        public ulong MetadataCount;
        public ulong DataOffset;
        public uint ContextLength;
        public uint EmbeddingLength;
        public uint SupportedTensorCount;
        public uint UnsupportedTensorCount;
        public uint DirectMlDeviceReady;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Architecture;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ModelName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string Message;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeSockJackDmlTensorDispatchOptions
    {
        public uint Size;
        public uint MaxElementCount;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string TensorName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeSockJackDmlTensorDispatchResult
    {
        public uint Size;
        public uint Status;
        public ulong TensorElementCount;
        public ulong DispatchedElementCount;
        public ulong TensorByteCount;
        public uint TensorType;
        public uint DimensionCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public ulong[] Dimensions;

        public uint PreviewCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public float[] InputPreview;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public float[] OutputPreview;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string TensorName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string Message;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint SockJackDmlProbeDelegate(nint result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint SockJackDmlRunIdentityFloat32Delegate(
        nint input,
        nint output,
        uint elementCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate uint SockJackDmlLoadModelDelegate(
        string modelPath,
        ref NativeSockJackDmlModelLoadOptions options,
        ref NativeSockJackDmlModelInfo info,
        out nint modelHandle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint SockJackDmlRunTensorIdentityFloat32Delegate(
        nint modelHandle,
        ref NativeSockJackDmlTensorDispatchOptions options,
        ref NativeSockJackDmlTensorDispatchResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate uint SockJackDmlUnloadModelDelegate(nint modelHandle);
}

