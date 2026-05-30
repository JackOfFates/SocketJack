#pragma once

#include <cstdint>

#ifdef SOCKJACKDML_EXPORTS
#define SOCKJACKDML_API extern "C" __declspec(dllexport)
#else
#define SOCKJACKDML_API extern "C" __declspec(dllimport)
#endif

constexpr std::uint32_t SOCKJACKDML_ABI_VERSION = 1;

enum SockJackDmlStatus : std::uint32_t
{
    SOCKJACKDML_OK = 0,
    SOCKJACKDML_INVALID_ARGUMENT = 1,
    SOCKJACKDML_DXGI_UNAVAILABLE = 2,
    SOCKJACKDML_NO_HARDWARE_ADAPTER = 3,
    SOCKJACKDML_D3D12_UNAVAILABLE = 4,
    SOCKJACKDML_DIRECTML_UNAVAILABLE = 5,
    SOCKJACKDML_NOT_IMPLEMENTED = 6,
    SOCKJACKDML_UNSUPPORTED_GGUF = 7,
    SOCKJACKDML_INTERNAL_ERROR = 100
};

struct SockJackDmlVersion
{
    std::uint32_t Size;
    std::uint32_t AbiVersion;
    std::uint32_t Major;
    std::uint32_t Minor;
    std::uint32_t Patch;
};

struct SockJackDmlProbeResult
{
    std::uint32_t Size;
    std::uint32_t Status;
    std::uint32_t NativeDirectMlAvailable;
    std::uint32_t AdapterIndex;
    std::uint64_t DedicatedVideoMemory;
    std::uint64_t SharedSystemMemory;
    wchar_t AdapterName[128];
    char Message[512];
};

struct SockJackDmlModelLoadOptions
{
    std::uint32_t Size;
    std::uint32_t ContextLength;
    std::int32_t GpuLayerCount;
};

struct SockJackDmlModelInfo
{
    std::uint32_t Size;
    std::uint32_t Status;
    std::uint64_t FileSize;
    std::uint64_t TensorCount;
    std::uint64_t MetadataCount;
    std::uint64_t DataOffset;
    std::uint32_t ContextLength;
    std::uint32_t EmbeddingLength;
    std::uint32_t SupportedTensorCount;
    std::uint32_t UnsupportedTensorCount;
    std::uint32_t DirectMlDeviceReady;
    char Architecture[64];
    char ModelName[128];
    char Message[512];
};

struct SockJackDmlTensorDispatchOptions
{
    std::uint32_t Size;
    std::uint32_t MaxElementCount;
    char TensorName[128];
};

struct SockJackDmlTensorDispatchResult
{
    std::uint32_t Size;
    std::uint32_t Status;
    std::uint64_t TensorElementCount;
    std::uint64_t DispatchedElementCount;
    std::uint64_t TensorByteCount;
    std::uint32_t TensorType;
    std::uint32_t DimensionCount;
    std::uint64_t Dimensions[8];
    std::uint32_t PreviewCount;
    float InputPreview[8];
    float OutputPreview[8];
    char TensorName[128];
    char Message[512];
};

SOCKJACKDML_API std::uint32_t SockJackDmlGetVersion(SockJackDmlVersion* version);
SOCKJACKDML_API std::uint32_t SockJackDmlProbe(SockJackDmlProbeResult* result);
SOCKJACKDML_API std::uint32_t SockJackDmlRunIdentityFloat32(const float* input, float* output, std::uint32_t elementCount);
SOCKJACKDML_API std::uint32_t SockJackDmlLoadModel(const wchar_t* modelPath, const SockJackDmlModelLoadOptions* options, SockJackDmlModelInfo* info, void** modelHandle);
SOCKJACKDML_API std::uint32_t SockJackDmlRunTensorIdentityFloat32(void* modelHandle, const SockJackDmlTensorDispatchOptions* options, SockJackDmlTensorDispatchResult* result);
SOCKJACKDML_API std::uint32_t SockJackDmlUnloadModel(void* modelHandle);
SOCKJACKDML_API const char* SockJackDmlGetLastError();

