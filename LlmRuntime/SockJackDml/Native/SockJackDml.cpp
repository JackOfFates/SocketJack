#include "SockJackDml.h"

#include <windows.h>
#include <DirectML.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cstdio>
#include <fstream>
#include <cstring>
#include <cwchar>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace
{
    std::mutex g_errorLock;
    std::string g_lastError;

    void Trace(const char* message)
    {
        char* enabled = nullptr;
        size_t enabledLength = 0;
        if (_dupenv_s(&enabled, &enabledLength, "SOCKJACKDML_TRACE") != 0 || enabled == nullptr)
        {
            return;
        }

        free(enabled);
        std::ofstream stream("Tools\\DirectML\\SockJackDml.trace.log", std::ios::app);
        stream << (message == nullptr ? "" : message) << std::endl;
    }

    void SetLastErrorMessage(const char* message)
    {
        std::lock_guard<std::mutex> guard(g_errorLock);
        g_lastError = message == nullptr ? "" : message;
    }

    void CopyMessage(char* destination, size_t destinationLength, const char* message)
    {
        if (destination == nullptr || destinationLength == 0)
        {
            return;
        }

        const char* value = message == nullptr ? "" : message;
        strncpy_s(destination, destinationLength, value, _TRUNCATE);
    }

    void CopyString(char* destination, size_t destinationLength, const std::string& message)
    {
        CopyMessage(destination, destinationLength, message.c_str());
    }

    void CopyAdapterName(wchar_t* destination, size_t destinationLength, const wchar_t* name)
    {
        if (destination == nullptr || destinationLength == 0)
        {
            return;
        }

        const wchar_t* value = name == nullptr ? L"" : name;
        wcsncpy_s(destination, destinationLength, value, _TRUNCATE);
    }

    std::string FormatHResult(const char* operation, HRESULT hr)
    {
        char buffer[256]{};
        std::snprintf(buffer, sizeof(buffer), "%s failed with HRESULT 0x%08X.", operation, static_cast<unsigned int>(hr));
        return std::string(buffer);
    }

    bool IsSoftwareAdapter(const DXGI_ADAPTER_DESC1& desc)
    {
        return (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
    }

    HRESULT CreateFactory(ComPtr<IDXGIFactory6>& factory)
    {
        HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
        if (SUCCEEDED(hr))
        {
            return hr;
        }

        ComPtr<IDXGIFactory1> factory1;
        hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory1));
        if (FAILED(hr))
        {
            return hr;
        }

        return factory1.As(&factory);
    }

    HRESULT TryCreateDevicesForAdapter(
        IDXGIAdapter1* adapter,
        ComPtr<ID3D12Device>& d3d12Device,
        ComPtr<IDMLDevice>& dmlDevice)
    {
        HRESULT hr = D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&d3d12Device));
        if (FAILED(hr))
        {
            return hr;
        }

        return DMLCreateDevice(d3d12Device.Get(), DML_CREATE_DEVICE_FLAG_NONE, IID_PPV_ARGS(&dmlDevice));
    }

    HRESULT FindDirectMlAdapter(
        ComPtr<IDXGIAdapter1>& adapter,
        DXGI_ADAPTER_DESC1& adapterDesc,
        std::uint32_t& adapterIndex);

    struct SockJackDmlContext
    {
        ComPtr<IDXGIAdapter1> Adapter;
        ComPtr<ID3D12Device> D3D12Device;
        ComPtr<IDMLDevice> DmlDevice;
        ComPtr<ID3D12CommandQueue> CommandQueue;
        ComPtr<ID3D12CommandAllocator> CommandAllocator;
        ComPtr<ID3D12GraphicsCommandList> CommandList;
        ComPtr<IDMLCommandRecorder> CommandRecorder;
        ComPtr<ID3D12Fence> Fence;
        HANDLE FenceEvent = nullptr;
        std::uint64_t FenceValue = 0;

        ~SockJackDmlContext()
        {
            if (FenceEvent != nullptr)
            {
                CloseHandle(FenceEvent);
                FenceEvent = nullptr;
            }
        }

        HRESULT Initialize()
        {
            DXGI_ADAPTER_DESC1 desc{};
            std::uint32_t adapterIndex = 0;
            HRESULT hr = FindDirectMlAdapter(Adapter, desc, adapterIndex);
            if (FAILED(hr))
            {
                return hr;
            }

            hr = TryCreateDevicesForAdapter(Adapter.Get(), D3D12Device, DmlDevice);
            if (FAILED(hr))
            {
                return hr;
            }

            D3D12_COMMAND_QUEUE_DESC queueDesc{};
            queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
            queueDesc.Priority = D3D12_COMMAND_QUEUE_PRIORITY_NORMAL;
            queueDesc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
            hr = D3D12Device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&CommandQueue));
            if (FAILED(hr))
            {
                return hr;
            }

            hr = D3D12Device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&CommandAllocator));
            if (FAILED(hr))
            {
                return hr;
            }

            hr = D3D12Device->CreateCommandList(
                0,
                D3D12_COMMAND_LIST_TYPE_DIRECT,
                CommandAllocator.Get(),
                nullptr,
                IID_PPV_ARGS(&CommandList));
            if (FAILED(hr))
            {
                return hr;
            }

            hr = D3D12Device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&Fence));
            if (FAILED(hr))
            {
                return hr;
            }

            FenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            return FenceEvent == nullptr ? HRESULT_FROM_WIN32(GetLastError()) : S_OK;
        }

        HRESULT ExecuteAndWait()
        {
            HRESULT hr = CommandList->Close();
            if (FAILED(hr))
            {
                return hr;
            }

            ID3D12CommandList* lists[] = { CommandList.Get() };
            CommandQueue->ExecuteCommandLists(1, lists);

            const std::uint64_t signalValue = ++FenceValue;
            hr = CommandQueue->Signal(Fence.Get(), signalValue);
            if (FAILED(hr))
            {
                return hr;
            }

            if (Fence->GetCompletedValue() < signalValue)
            {
                hr = Fence->SetEventOnCompletion(signalValue, FenceEvent);
                if (FAILED(hr))
                {
                    return hr;
                }

                WaitForSingleObject(FenceEvent, INFINITE);
            }

            return S_OK;
        }

        HRESULT ResetCommandList()
        {
            HRESULT hr = CommandAllocator->Reset();
            if (FAILED(hr))
            {
                return hr;
            }

            return CommandList->Reset(CommandAllocator.Get(), nullptr);
        }

        HRESULT RecordDirectMlDispatch(IDMLDispatchable* dispatchable, IDMLBindingTable* bindingTable)
        {
            if (CommandRecorder == nullptr)
            {
                HRESULT hr = DmlDevice->CreateCommandRecorder(IID_PPV_ARGS(&CommandRecorder));
                if (FAILED(hr))
                {
                    return hr;
                }
            }

            CommandRecorder->RecordDispatch(CommandList.Get(), dispatchable, bindingTable);
            return S_OK;
        }
    };

    D3D12_HEAP_PROPERTIES HeapProperties(D3D12_HEAP_TYPE type)
    {
        D3D12_HEAP_PROPERTIES properties{};
        properties.Type = type;
        properties.CPUPageProperty = D3D12_CPU_PAGE_PROPERTY_UNKNOWN;
        properties.MemoryPoolPreference = D3D12_MEMORY_POOL_UNKNOWN;
        properties.CreationNodeMask = 1;
        properties.VisibleNodeMask = 1;
        return properties;
    }

    D3D12_RESOURCE_DESC BufferResourceDesc(std::uint64_t sizeInBytes, D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE)
    {
        D3D12_RESOURCE_DESC desc{};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Alignment = 0;
        desc.Width = sizeInBytes;
        desc.Height = 1;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT_UNKNOWN;
        desc.SampleDesc.Count = 1;
        desc.SampleDesc.Quality = 0;
        desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        desc.Flags = flags;
        return desc;
    }

    HRESULT CreateBuffer(
        ID3D12Device* device,
        D3D12_HEAP_TYPE heapType,
        std::uint64_t sizeInBytes,
        D3D12_RESOURCE_STATES initialState,
        D3D12_RESOURCE_FLAGS flags,
        ComPtr<ID3D12Resource>& resource)
    {
        D3D12_HEAP_PROPERTIES heap = HeapProperties(heapType);
        D3D12_RESOURCE_DESC desc = BufferResourceDesc(sizeInBytes, flags);
        return device->CreateCommittedResource(
            &heap,
            D3D12_HEAP_FLAG_NONE,
            &desc,
            initialState,
            nullptr,
            IID_PPV_ARGS(&resource));
    }

    D3D12_RESOURCE_BARRIER TransitionBarrier(
        ID3D12Resource* resource,
        D3D12_RESOURCE_STATES before,
        D3D12_RESOURCE_STATES after)
    {
        D3D12_RESOURCE_BARRIER barrier{};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
        barrier.Transition.pResource = resource;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = before;
        barrier.Transition.StateAfter = after;
        return barrier;
    }

    HRESULT FillUploadBuffer(ID3D12Resource* uploadBuffer, const void* source, std::uint64_t byteCount)
    {
        void* mapped = nullptr;
        D3D12_RANGE readRange{ 0, 0 };
        HRESULT hr = uploadBuffer->Map(0, &readRange, &mapped);
        if (FAILED(hr))
        {
            return hr;
        }

        std::memcpy(mapped, source, static_cast<size_t>(byteCount));
        D3D12_RANGE writtenRange{ 0, static_cast<SIZE_T>(byteCount) };
        uploadBuffer->Unmap(0, &writtenRange);
        return S_OK;
    }

    HRESULT ReadBackBuffer(ID3D12Resource* readbackBuffer, void* destination, std::uint64_t byteCount)
    {
        void* mapped = nullptr;
        D3D12_RANGE readRange{ 0, static_cast<SIZE_T>(byteCount) };
        HRESULT hr = readbackBuffer->Map(0, &readRange, &mapped);
        if (FAILED(hr))
        {
            return hr;
        }

        std::memcpy(destination, mapped, static_cast<size_t>(byteCount));
        D3D12_RANGE writtenRange{ 0, 0 };
        readbackBuffer->Unmap(0, &writtenRange);
        return S_OK;
    }

    HRESULT CreateUavBuffer(
        ID3D12Device* device,
        std::uint64_t sizeInBytes,
        D3D12_RESOURCE_STATES initialState,
        ComPtr<ID3D12Resource>& resource)
    {
        return CreateBuffer(
            device,
            D3D12_HEAP_TYPE_DEFAULT,
            sizeInBytes,
            initialState,
            D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            resource);
    }

    DML_BUFFER_BINDING BufferBinding(ID3D12Resource* resource, std::uint64_t byteCount)
    {
        DML_BUFFER_BINDING binding{};
        binding.Buffer = resource;
        binding.Offset = 0;
        binding.SizeInBytes = byteCount;
        return binding;
    }

    DML_BINDING_DESC BindingDesc(const DML_BUFFER_BINDING& binding)
    {
        DML_BINDING_DESC desc{};
        desc.Type = DML_BINDING_TYPE_BUFFER;
        desc.Desc = &binding;
        return desc;
    }

    struct FileMappingView
    {
        HANDLE File = INVALID_HANDLE_VALUE;
        HANDLE Mapping = nullptr;
        const std::uint8_t* View = nullptr;
        std::uint64_t Size = 0;

        ~FileMappingView()
        {
            if (View != nullptr)
            {
                UnmapViewOfFile(View);
                View = nullptr;
            }

            if (Mapping != nullptr)
            {
                CloseHandle(Mapping);
                Mapping = nullptr;
            }

            if (File != INVALID_HANDLE_VALUE)
            {
                CloseHandle(File);
                File = INVALID_HANDLE_VALUE;
            }
        }
    };

    struct GgufTensorInfo
    {
        std::string Name;
        std::vector<std::uint64_t> Dimensions;
        std::uint32_t Type = 0;
        std::uint64_t Offset = 0;
    };

    struct SockJackDmlModel
    {
        FileMappingView Mapping;
        std::unique_ptr<SockJackDmlContext> Context;
        std::string Architecture;
        std::string ModelName;
        std::uint64_t TensorCount = 0;
        std::uint64_t MetadataCount = 0;
        std::uint64_t DataOffset = 0;
        std::uint32_t ContextLength = 0;
        std::uint32_t EmbeddingLength = 0;
        std::uint32_t SupportedTensorCount = 0;
        std::uint32_t UnsupportedTensorCount = 0;
        std::vector<GgufTensorInfo> Tensors;
    };

    struct GgufReader
    {
        const std::uint8_t* Data = nullptr;
        std::uint64_t Size = 0;
        std::uint64_t Position = 0;
        std::string Error;
    };

    enum GgufValueType : std::uint32_t
    {
        GgufUint8 = 0,
        GgufInt8 = 1,
        GgufUint16 = 2,
        GgufInt16 = 3,
        GgufUint32 = 4,
        GgufInt32 = 5,
        GgufFloat32 = 6,
        GgufBool = 7,
        GgufString = 8,
        GgufArray = 9,
        GgufUint64 = 10,
        GgufInt64 = 11,
        GgufFloat64 = 12
    };

    bool OpenReadOnlyMapping(const wchar_t* path, FileMappingView& mapping, std::string& error)
    {
        mapping.File = CreateFileW(
            path,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (mapping.File == INVALID_HANDLE_VALUE)
        {
            error = FormatHResult("CreateFileW", HRESULT_FROM_WIN32(GetLastError()));
            return false;
        }

        LARGE_INTEGER size{};
        if (!GetFileSizeEx(mapping.File, &size) || size.QuadPart <= 0)
        {
            error = "GGUF file is empty or its size could not be read.";
            return false;
        }

        mapping.Size = static_cast<std::uint64_t>(size.QuadPart);
        mapping.Mapping = CreateFileMappingW(mapping.File, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (mapping.Mapping == nullptr)
        {
            error = FormatHResult("CreateFileMappingW", HRESULT_FROM_WIN32(GetLastError()));
            return false;
        }

        mapping.View = static_cast<const std::uint8_t*>(MapViewOfFile(mapping.Mapping, FILE_MAP_READ, 0, 0, 0));
        if (mapping.View == nullptr)
        {
            error = FormatHResult("MapViewOfFile", HRESULT_FROM_WIN32(GetLastError()));
            return false;
        }

        return true;
    }

    template <typename T>
    bool Read(GgufReader& reader, T& value)
    {
        if (reader.Position > reader.Size || sizeof(T) > reader.Size - reader.Position)
        {
            reader.Error = "Unexpected end of GGUF file.";
            return false;
        }

        std::memcpy(&value, reader.Data + reader.Position, sizeof(T));
        reader.Position += sizeof(T);
        return true;
    }

    bool SkipBytes(GgufReader& reader, std::uint64_t byteCount)
    {
        if (reader.Position > reader.Size || byteCount > reader.Size - reader.Position)
        {
            reader.Error = "Unexpected end of GGUF file while skipping data.";
            return false;
        }

        reader.Position += byteCount;
        return true;
    }

    bool ReadString(GgufReader& reader, std::string& value)
    {
        std::uint64_t length = 0;
        if (!Read(reader, length))
        {
            return false;
        }

        if (length > reader.Size - reader.Position)
        {
            reader.Error = "GGUF string length exceeds remaining file size.";
            return false;
        }

        value.assign(reinterpret_cast<const char*>(reader.Data + reader.Position), static_cast<size_t>(length));
        reader.Position += length;
        return true;
    }

    bool SkipString(GgufReader& reader)
    {
        std::uint64_t length = 0;
        return Read(reader, length) && SkipBytes(reader, length);
    }

    bool SkipMetadataValue(GgufReader& reader, std::uint32_t type);

    bool SkipArray(GgufReader& reader)
    {
        std::uint32_t itemType = 0;
        std::uint64_t count = 0;
        if (!Read(reader, itemType) || !Read(reader, count))
        {
            return false;
        }

        if (count > 100000000ULL)
        {
            reader.Error = "GGUF metadata array is too large.";
            return false;
        }

        for (std::uint64_t index = 0; index < count; index++)
        {
            if (!SkipMetadataValue(reader, itemType))
            {
                return false;
            }
        }

        return true;
    }

    bool SkipMetadataValue(GgufReader& reader, std::uint32_t type)
    {
        switch (type)
        {
        case GgufUint8:
        case GgufInt8:
        case GgufBool:
            return SkipBytes(reader, 1);
        case GgufUint16:
        case GgufInt16:
            return SkipBytes(reader, 2);
        case GgufUint32:
        case GgufInt32:
        case GgufFloat32:
            return SkipBytes(reader, 4);
        case GgufUint64:
        case GgufInt64:
        case GgufFloat64:
            return SkipBytes(reader, 8);
        case GgufString:
            return SkipString(reader);
        case GgufArray:
            return SkipArray(reader);
        default:
            reader.Error = "Unsupported GGUF metadata value type.";
            return false;
        }
    }

    bool ReadMetadataUnsigned(GgufReader& reader, std::uint32_t type, std::uint64_t& value)
    {
        switch (type)
        {
        case GgufUint8:
        {
            std::uint8_t typed = 0;
            if (!Read(reader, typed)) return false;
            value = typed;
            return true;
        }
        case GgufUint16:
        {
            std::uint16_t typed = 0;
            if (!Read(reader, typed)) return false;
            value = typed;
            return true;
        }
        case GgufUint32:
        {
            std::uint32_t typed = 0;
            if (!Read(reader, typed)) return false;
            value = typed;
            return true;
        }
        case GgufUint64:
            return Read(reader, value);
        default:
            return SkipMetadataValue(reader, type) && false;
        }
    }

    bool IsSupportedGgmlTensorType(std::uint32_t type)
    {
        switch (type)
        {
        case 0:  // F32
        case 1:  // F16
        case 2:  // Q4_0
        case 8:  // Q8_0
        case 12: // Q4_K
        case 13: // Q5_K
            return true;
        default:
            return false;
        }
    }

    std::uint64_t AlignTo(std::uint64_t value, std::uint64_t alignment)
    {
        if (alignment == 0)
        {
            return value;
        }

        const std::uint64_t remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    bool ParseGguf(SockJackDmlModel& model, std::string& error)
    {
        GgufReader reader{ model.Mapping.View, model.Mapping.Size, 0, "" };

        std::uint32_t magic = 0;
        std::uint32_t version = 0;
        if (!Read(reader, magic) || !Read(reader, version))
        {
            error = reader.Error;
            return false;
        }

        if (magic != 0x46554747)
        {
            error = "File is not a GGUF file.";
            return false;
        }

        if (version < 2 || version > 3)
        {
            error = "Unsupported GGUF version.";
            return false;
        }

        std::uint64_t tensorCount = 0;
        std::uint64_t metadataCount = 0;
        if (!Read(reader, tensorCount) || !Read(reader, metadataCount))
        {
            error = reader.Error;
            return false;
        }

        model.TensorCount = tensorCount;
        model.MetadataCount = metadataCount;

        std::uint64_t alignment = 32;
        for (std::uint64_t index = 0; index < metadataCount; index++)
        {
            std::string key;
            std::uint32_t type = 0;
            if (!ReadString(reader, key) || !Read(reader, type))
            {
                error = reader.Error;
                return false;
            }

            if ((key == "general.architecture" || key == "general.name") && type == GgufString)
            {
                std::string value;
                if (!ReadString(reader, value))
                {
                    error = reader.Error;
                    return false;
                }

                if (key == "general.architecture")
                {
                    model.Architecture = value;
                }
                else
                {
                    model.ModelName = value;
                }
                continue;
            }

            if (key == "general.alignment" || key == "llama.context_length" || key == "llama.embedding_length")
            {
                std::uint64_t value = 0;
                if (!ReadMetadataUnsigned(reader, type, value))
                {
                    error = reader.Error.empty() ? "GGUF metadata value has an unsupported type." : reader.Error;
                    return false;
                }

                if (key == "general.alignment")
                {
                    alignment = value == 0 ? 32 : value;
                }
                else if (key == "llama.context_length")
                {
                    model.ContextLength = static_cast<std::uint32_t>((std::min)(value, static_cast<std::uint64_t>((std::numeric_limits<std::uint32_t>::max)())));
                }
                else
                {
                    model.EmbeddingLength = static_cast<std::uint32_t>((std::min)(value, static_cast<std::uint64_t>((std::numeric_limits<std::uint32_t>::max)())));
                }
                continue;
            }

            if (!SkipMetadataValue(reader, type))
            {
                error = reader.Error;
                return false;
            }
        }

        if (tensorCount > 1000000ULL)
        {
            error = "GGUF tensor table is too large.";
            return false;
        }

        model.Tensors.reserve(static_cast<size_t>((std::min)(tensorCount, 1000000ULL)));
        for (std::uint64_t index = 0; index < tensorCount; index++)
        {
            GgufTensorInfo tensor;
            std::uint32_t dimensions = 0;
            if (!ReadString(reader, tensor.Name) || !Read(reader, dimensions))
            {
                error = reader.Error;
                return false;
            }

            if (dimensions == 0 || dimensions > 8)
            {
                error = "GGUF tensor has an invalid dimension count.";
                return false;
            }

            tensor.Dimensions.resize(dimensions);
            for (std::uint32_t dimension = 0; dimension < dimensions; dimension++)
            {
                if (!Read(reader, tensor.Dimensions[dimension]))
                {
                    error = reader.Error;
                    return false;
                }
            }

            if (!Read(reader, tensor.Type) || !Read(reader, tensor.Offset))
            {
                error = reader.Error;
                return false;
            }

            if (IsSupportedGgmlTensorType(tensor.Type))
            {
                model.SupportedTensorCount++;
            }
            else
            {
                model.UnsupportedTensorCount++;
            }

            model.Tensors.push_back(std::move(tensor));
        }

        model.DataOffset = AlignTo(reader.Position, alignment);
        if (model.DataOffset > model.Mapping.Size)
        {
            error = "GGUF tensor data offset exceeds file size.";
            return false;
        }

        for (const GgufTensorInfo& tensor : model.Tensors)
        {
            if (tensor.Offset > model.Mapping.Size - model.DataOffset)
            {
                error = "GGUF tensor offset exceeds file size.";
                return false;
            }
        }

        return true;
    }

    void FillModelInfo(SockJackDmlModelInfo* info, const SockJackDmlModel& model, std::uint32_t status, const char* message)
    {
        if (info == nullptr)
        {
            return;
        }

        info->Status = status;
        info->FileSize = model.Mapping.Size;
        info->TensorCount = model.TensorCount;
        info->MetadataCount = model.MetadataCount;
        info->DataOffset = model.DataOffset;
        info->ContextLength = model.ContextLength;
        info->EmbeddingLength = model.EmbeddingLength;
        info->SupportedTensorCount = model.SupportedTensorCount;
        info->UnsupportedTensorCount = model.UnsupportedTensorCount;
        info->DirectMlDeviceReady = model.Context == nullptr ? 0 : 1;
        CopyString(info->Architecture, std::size(info->Architecture), model.Architecture);
        CopyString(info->ModelName, std::size(info->ModelName), model.ModelName);
        CopyMessage(info->Message, std::size(info->Message), message);
    }

    bool TryCalculateTensorElementCount(const GgufTensorInfo& tensor, std::uint64_t& elementCount, std::string& error)
    {
        elementCount = 1;
        for (std::uint64_t dimension : tensor.Dimensions)
        {
            if (dimension == 0)
            {
                error = "GGUF tensor has a zero-sized dimension.";
                return false;
            }

            if (elementCount > (std::numeric_limits<std::uint64_t>::max)() / dimension)
            {
                error = "GGUF tensor element count overflowed.";
                return false;
            }

            elementCount *= dimension;
        }

        return true;
    }

    bool TryCalculateTensorByteCount(const GgufTensorInfo& tensor, std::uint64_t& elementCount, std::uint64_t& byteCount, std::string& error)
    {
        if (!TryCalculateTensorElementCount(tensor, elementCount, error))
        {
            return false;
        }

        if (tensor.Type != 0)
        {
            error = "Only F32 tensors are supported by the current native tensor identity smoke dispatch.";
            return false;
        }

        if (elementCount > (std::numeric_limits<std::uint64_t>::max)() / sizeof(float))
        {
            error = "GGUF tensor byte count overflowed.";
            return false;
        }

        byteCount = elementCount * sizeof(float);
        return true;
    }

    const GgufTensorInfo* FindFloat32Tensor(const SockJackDmlModel& model, const char* requestedName)
    {
        const bool hasRequestedName = requestedName != nullptr && requestedName[0] != '\0';
        for (const GgufTensorInfo& tensor : model.Tensors)
        {
            if (tensor.Type != 0)
            {
                continue;
            }

            if (!hasRequestedName || tensor.Name == requestedName)
            {
                return &tensor;
            }
        }

        return nullptr;
    }

    HRESULT RunIdentityFloat32Dispatch(const float* input, float* output, std::uint32_t elementCount)
    {
        Trace("identity: begin");
        SockJackDmlContext context;
        HRESULT hr = context.Initialize();
        if (FAILED(hr))
        {
            return hr;
        }
        Trace("identity: context initialized");

        const std::uint64_t byteCount = static_cast<std::uint64_t>(elementCount) * sizeof(float);
        ComPtr<ID3D12Resource> uploadBuffer;
        ComPtr<ID3D12Resource> inputBuffer;
        ComPtr<ID3D12Resource> outputBuffer;
        ComPtr<ID3D12Resource> readbackBuffer;

        hr = CreateBuffer(context.D3D12Device.Get(), D3D12_HEAP_TYPE_UPLOAD, byteCount, D3D12_RESOURCE_STATE_GENERIC_READ, D3D12_RESOURCE_FLAG_NONE, uploadBuffer);
        if (FAILED(hr)) return hr;
        hr = CreateUavBuffer(context.D3D12Device.Get(), byteCount, D3D12_RESOURCE_STATE_COPY_DEST, inputBuffer);
        if (FAILED(hr)) return hr;
        hr = CreateUavBuffer(context.D3D12Device.Get(), byteCount, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, outputBuffer);
        if (FAILED(hr)) return hr;
        hr = CreateBuffer(context.D3D12Device.Get(), D3D12_HEAP_TYPE_READBACK, byteCount, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE, readbackBuffer);
        if (FAILED(hr)) return hr;

        hr = FillUploadBuffer(uploadBuffer.Get(), input, byteCount);
        if (FAILED(hr)) return hr;
        Trace("identity: buffers ready");

        UINT sizes[] = { elementCount };
        DML_BUFFER_TENSOR_DESC inputTensorDesc{};
        inputTensorDesc.DataType = DML_TENSOR_DATA_TYPE_FLOAT32;
        inputTensorDesc.Flags = DML_TENSOR_FLAG_NONE;
        inputTensorDesc.DimensionCount = 1;
        inputTensorDesc.Sizes = sizes;
        inputTensorDesc.Strides = nullptr;
        inputTensorDesc.TotalTensorSizeInBytes = byteCount;
        inputTensorDesc.GuaranteedBaseOffsetAlignment = 0;

        DML_BUFFER_TENSOR_DESC outputTensorDesc = inputTensorDesc;

        DML_TENSOR_DESC inputTensor{};
        inputTensor.Type = DML_TENSOR_TYPE_BUFFER;
        inputTensor.Desc = &inputTensorDesc;

        DML_TENSOR_DESC outputTensor{};
        outputTensor.Type = DML_TENSOR_TYPE_BUFFER;
        outputTensor.Desc = &outputTensorDesc;

        DML_ELEMENT_WISE_IDENTITY_OPERATOR_DESC identityDesc{};
        identityDesc.InputTensor = &inputTensor;
        identityDesc.OutputTensor = &outputTensor;
        identityDesc.ScaleBias = nullptr;

        DML_OPERATOR_DESC operatorDesc{};
        operatorDesc.Type = DML_OPERATOR_ELEMENT_WISE_IDENTITY;
        operatorDesc.Desc = &identityDesc;

        ComPtr<IDMLOperator> op;
        hr = context.DmlDevice->CreateOperator(&operatorDesc, IID_PPV_ARGS(&op));
        if (FAILED(hr)) return hr;
        Trace("identity: operator created");

        ComPtr<IDMLCompiledOperator> compiled;
        hr = context.DmlDevice->CompileOperator(op.Get(), DML_EXECUTION_FLAG_NONE, IID_PPV_ARGS(&compiled));
        if (FAILED(hr)) return hr;
        Trace("identity: operator compiled");

        IDMLCompiledOperator* compiledOperators[] = { compiled.Get() };
        ComPtr<IDMLOperatorInitializer> initializer;
        hr = context.DmlDevice->CreateOperatorInitializer(1, compiledOperators, IID_PPV_ARGS(&initializer));
        if (FAILED(hr)) return hr;
        Trace("identity: initializer created");

        DML_BINDING_PROPERTIES compiledProps = compiled->GetBindingProperties();
        DML_BINDING_PROPERTIES initializerProps = initializer->GetBindingProperties();
        UINT descriptorCount = (std::max)(compiledProps.RequiredDescriptorCount, initializerProps.RequiredDescriptorCount);
        descriptorCount = (std::max)(descriptorCount, static_cast<UINT>(1));

        D3D12_DESCRIPTOR_HEAP_DESC heapDesc{};
        heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heapDesc.NumDescriptors = descriptorCount;
        heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

        ComPtr<ID3D12DescriptorHeap> descriptorHeap;
        hr = context.D3D12Device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&descriptorHeap));
        if (FAILED(hr)) return hr;
        Trace("identity: descriptor heap created");

        ComPtr<IDMLBindingTable> bindingTable;
        DML_BINDING_TABLE_DESC bindingTableDesc{};
        bindingTableDesc.Dispatchable = initializer.Get();
        bindingTableDesc.CPUDescriptorHandle = descriptorHeap->GetCPUDescriptorHandleForHeapStart();
        bindingTableDesc.GPUDescriptorHandle = descriptorHeap->GetGPUDescriptorHandleForHeapStart();
        bindingTableDesc.SizeInDescriptors = descriptorCount;
        hr = context.DmlDevice->CreateBindingTable(&bindingTableDesc, IID_PPV_ARGS(&bindingTable));
        if (FAILED(hr)) return hr;
        Trace("identity: binding table created");

        ComPtr<ID3D12Resource> initializerTemporary;
        ComPtr<ID3D12Resource> persistentResource;
        if (compiledProps.PersistentResourceSize > 0)
        {
            hr = CreateUavBuffer(context.D3D12Device.Get(), compiledProps.PersistentResourceSize, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, persistentResource);
            if (FAILED(hr)) return hr;
        }

        if (initializerProps.TemporaryResourceSize > 0)
        {
            hr = CreateUavBuffer(context.D3D12Device.Get(), initializerProps.TemporaryResourceSize, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, initializerTemporary);
            if (FAILED(hr)) return hr;
            DML_BUFFER_BINDING tempBinding = BufferBinding(initializerTemporary.Get(), initializerProps.TemporaryResourceSize);
            DML_BINDING_DESC tempDesc = BindingDesc(tempBinding);
            Trace("identity: binding initializer temporary");
            bindingTable->BindTemporaryResource(&tempDesc);
        }

        if (persistentResource)
        {
            DML_BUFFER_BINDING persistentBinding = BufferBinding(persistentResource.Get(), compiledProps.PersistentResourceSize);
            DML_BINDING_DESC persistentDesc = BindingDesc(persistentBinding);
            Trace("identity: binding initializer persistent output");
            bindingTable->BindOutputs(1, &persistentDesc);
        }

        ID3D12DescriptorHeap* heaps[] = { descriptorHeap.Get() };
        context.CommandList->SetDescriptorHeaps(1, heaps);

        Trace("identity: recording initializer dispatch");
        hr = context.RecordDirectMlDispatch(initializer.Get(), bindingTable.Get());
        if (FAILED(hr)) return hr;
        Trace("identity: initializer recorded");

        hr = context.ExecuteAndWait();
        if (FAILED(hr)) return hr;
        Trace("identity: initializer executed");

        hr = context.ResetCommandList();
        if (FAILED(hr)) return hr;

        context.CommandList->SetDescriptorHeaps(1, heaps);

        context.CommandList->CopyBufferRegion(inputBuffer.Get(), 0, uploadBuffer.Get(), 0, byteCount);
        D3D12_RESOURCE_BARRIER inputReady = TransitionBarrier(inputBuffer.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        context.CommandList->ResourceBarrier(1, &inputReady);

        bindingTableDesc.Dispatchable = compiled.Get();
        bindingTable->Reset(&bindingTableDesc);

        ComPtr<ID3D12Resource> operatorTemporary;
        if (compiledProps.TemporaryResourceSize > 0)
        {
            hr = CreateUavBuffer(context.D3D12Device.Get(), compiledProps.TemporaryResourceSize, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, operatorTemporary);
            if (FAILED(hr)) return hr;
            DML_BUFFER_BINDING tempBinding = BufferBinding(operatorTemporary.Get(), compiledProps.TemporaryResourceSize);
            DML_BINDING_DESC tempDesc = BindingDesc(tempBinding);
            Trace("identity: binding operator temporary");
            bindingTable->BindTemporaryResource(&tempDesc);
        }

        if (persistentResource)
        {
            DML_BUFFER_BINDING persistentBinding = BufferBinding(persistentResource.Get(), compiledProps.PersistentResourceSize);
            DML_BINDING_DESC persistentDesc = BindingDesc(persistentBinding);
            Trace("identity: binding operator persistent");
            bindingTable->BindPersistentResource(&persistentDesc);
        }

        DML_BUFFER_BINDING inputBinding = BufferBinding(inputBuffer.Get(), byteCount);
        DML_BUFFER_BINDING outputBinding = BufferBinding(outputBuffer.Get(), byteCount);
        DML_BINDING_DESC inputBindingDesc = BindingDesc(inputBinding);
        DML_BINDING_DESC outputBindingDesc = BindingDesc(outputBinding);
        Trace("identity: binding operator inputs and outputs");
        bindingTable->BindInputs(1, &inputBindingDesc);
        bindingTable->BindOutputs(1, &outputBindingDesc);

        hr = context.RecordDirectMlDispatch(compiled.Get(), bindingTable.Get());
        if (FAILED(hr)) return hr;
        Trace("identity: operator recorded");

        D3D12_RESOURCE_BARRIER outputUav{};
        outputUav.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        outputUav.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
        outputUav.UAV.pResource = outputBuffer.Get();
        context.CommandList->ResourceBarrier(1, &outputUav);

        D3D12_RESOURCE_BARRIER outputReady = TransitionBarrier(outputBuffer.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE);
        context.CommandList->ResourceBarrier(1, &outputReady);
        context.CommandList->CopyBufferRegion(readbackBuffer.Get(), 0, outputBuffer.Get(), 0, byteCount);

        hr = context.ExecuteAndWait();
        if (FAILED(hr)) return hr;
        Trace("identity: operator executed");

        hr = ReadBackBuffer(readbackBuffer.Get(), output, byteCount);
        if (SUCCEEDED(hr))
        {
            Trace("identity: readback complete");
        }
        return hr;
    }

    HRESULT FindDirectMlAdapter(
        ComPtr<IDXGIAdapter1>& adapter,
        DXGI_ADAPTER_DESC1& adapterDesc,
        std::uint32_t& adapterIndex)
    {
        ComPtr<IDXGIFactory6> factory;
        HRESULT hr = CreateFactory(factory);
        if (FAILED(hr))
        {
            return hr;
        }

        for (std::uint32_t index = 0;; index++)
        {
            ComPtr<IDXGIAdapter1> candidate;
            hr = factory->EnumAdapterByGpuPreference(index, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&candidate));
            if (hr == DXGI_ERROR_NOT_FOUND)
            {
                break;
            }
            if (FAILED(hr))
            {
                break;
            }

            DXGI_ADAPTER_DESC1 desc{};
            if (FAILED(candidate->GetDesc1(&desc)) || IsSoftwareAdapter(desc))
            {
                continue;
            }

            ComPtr<ID3D12Device> d3d12Device;
            ComPtr<IDMLDevice> dmlDevice;
            if (SUCCEEDED(TryCreateDevicesForAdapter(candidate.Get(), d3d12Device, dmlDevice)))
            {
                adapter = candidate;
                adapterDesc = desc;
                adapterIndex = index;
                return S_OK;
            }
        }

        ComPtr<IDXGIFactory1> factory1;
        hr = factory.As(&factory1);
        if (FAILED(hr))
        {
            return hr;
        }

        for (std::uint32_t index = 0;; index++)
        {
            ComPtr<IDXGIAdapter1> candidate;
            hr = factory1->EnumAdapters1(index, &candidate);
            if (hr == DXGI_ERROR_NOT_FOUND)
            {
                return DXGI_ERROR_NOT_FOUND;
            }
            if (FAILED(hr))
            {
                return hr;
            }

            DXGI_ADAPTER_DESC1 desc{};
            if (FAILED(candidate->GetDesc1(&desc)) || IsSoftwareAdapter(desc))
            {
                continue;
            }

            ComPtr<ID3D12Device> d3d12Device;
            ComPtr<IDMLDevice> dmlDevice;
            if (SUCCEEDED(TryCreateDevicesForAdapter(candidate.Get(), d3d12Device, dmlDevice)))
            {
                adapter = candidate;
                adapterDesc = desc;
                adapterIndex = index;
                return S_OK;
            }
        }
    }
}

std::uint32_t SockJackDmlGetVersion(SockJackDmlVersion* version)
{
    if (version == nullptr || version->Size < sizeof(SockJackDmlVersion))
    {
        SetLastErrorMessage("SockJackDmlGetVersion requires a valid SockJackDmlVersion pointer with Size initialized.");
        return SOCKJACKDML_INVALID_ARGUMENT;
    }

    version->AbiVersion = SOCKJACKDML_ABI_VERSION;
    version->Major = 0;
    version->Minor = 1;
    version->Patch = 0;
    SetLastErrorMessage("");
    return SOCKJACKDML_OK;
}

std::uint32_t SockJackDmlProbe(SockJackDmlProbeResult* result)
{
    if (result == nullptr || result->Size < sizeof(SockJackDmlProbeResult))
    {
        SetLastErrorMessage("SockJackDmlProbe requires a valid SockJackDmlProbeResult pointer with Size initialized.");
        return SOCKJACKDML_INVALID_ARGUMENT;
    }

    result->Status = SOCKJACKDML_INTERNAL_ERROR;
    result->NativeDirectMlAvailable = 0;
    result->AdapterIndex = 0;
    result->DedicatedVideoMemory = 0;
    result->SharedSystemMemory = 0;
    CopyAdapterName(result->AdapterName, std::size(result->AdapterName), L"");
    CopyMessage(result->Message, std::size(result->Message), "");

    ComPtr<IDXGIAdapter1> adapter;
    DXGI_ADAPTER_DESC1 desc{};
    std::uint32_t adapterIndex = 0;
    HRESULT hr = FindDirectMlAdapter(adapter, desc, adapterIndex);
    if (FAILED(hr))
    {
        std::string message = hr == DXGI_ERROR_NOT_FOUND
            ? "No hardware adapter with Direct3D 12 and DirectML support was found."
            : FormatHResult("DirectML adapter probe", hr);
        CopyMessage(result->Message, std::size(result->Message), message.c_str());
        result->Status = hr == DXGI_ERROR_NOT_FOUND ? SOCKJACKDML_NO_HARDWARE_ADAPTER : SOCKJACKDML_DIRECTML_UNAVAILABLE;
        SetLastErrorMessage(message.c_str());
        return result->Status;
    }

    result->Status = SOCKJACKDML_OK;
    result->NativeDirectMlAvailable = 1;
    result->AdapterIndex = adapterIndex;
    result->DedicatedVideoMemory = desc.DedicatedVideoMemory;
    result->SharedSystemMemory = desc.SharedSystemMemory;
    CopyAdapterName(result->AdapterName, std::size(result->AdapterName), desc.Description);
    CopyMessage(result->Message, std::size(result->Message), "Direct3D 12 and DirectML device creation succeeded.");
    SetLastErrorMessage("");
    return SOCKJACKDML_OK;
}

std::uint32_t SockJackDmlRunIdentityFloat32(const float* input, float* output, std::uint32_t elementCount)
{
    if (input == nullptr || output == nullptr || elementCount == 0)
    {
        SetLastErrorMessage("SockJackDmlRunIdentityFloat32 requires non-null input/output buffers and at least one element.");
        return SOCKJACKDML_INVALID_ARGUMENT;
    }

    if (elementCount > (std::numeric_limits<std::uint32_t>::max)() / static_cast<std::uint32_t>(sizeof(float)))
    {
        SetLastErrorMessage("SockJackDmlRunIdentityFloat32 element count is too large.");
        return SOCKJACKDML_INVALID_ARGUMENT;
    }

    HRESULT hr = RunIdentityFloat32Dispatch(input, output, elementCount);
    if (FAILED(hr))
    {
        std::string message = FormatHResult("SockJackDmlRunIdentityFloat32", hr);
        SetLastErrorMessage(message.c_str());
        return SOCKJACKDML_DIRECTML_UNAVAILABLE;
    }

    SetLastErrorMessage("");
    return SOCKJACKDML_OK;
}

std::uint32_t SockJackDmlLoadModel(
    const wchar_t* modelPath,
    const SockJackDmlModelLoadOptions* options,
    SockJackDmlModelInfo* info,
    void** modelHandle)
{
    if (modelPath == nullptr || modelPath[0] == L'\0' || info == nullptr || modelHandle == nullptr || info->Size < sizeof(SockJackDmlModelInfo))
    {
        SetLastErrorMessage("SockJackDmlLoadModel requires a model path, model info with Size initialized, and an output model handle.");
        return SOCKJACKDML_INVALID_ARGUMENT;
    }

    if (options != nullptr && options->Size < sizeof(SockJackDmlModelLoadOptions))
    {
        SetLastErrorMessage("SockJackDmlLoadModel options Size is invalid.");
        return SOCKJACKDML_INVALID_ARGUMENT;
    }

    *modelHandle = nullptr;
    const std::uint32_t size = info->Size;
    std::memset(info, 0, sizeof(SockJackDmlModelInfo));
    info->Size = size;
    info->Status = SOCKJACKDML_INTERNAL_ERROR;

    std::unique_ptr<SockJackDmlModel> model = std::make_unique<SockJackDmlModel>();
    std::string error;
    if (!OpenReadOnlyMapping(modelPath, model->Mapping, error))
    {
        FillModelInfo(info, *model, SOCKJACKDML_INVALID_ARGUMENT, error.c_str());
        SetLastErrorMessage(error.c_str());
        return SOCKJACKDML_INVALID_ARGUMENT;
    }

    if (!ParseGguf(*model, error))
    {
        FillModelInfo(info, *model, SOCKJACKDML_UNSUPPORTED_GGUF, error.c_str());
        SetLastErrorMessage(error.c_str());
        return SOCKJACKDML_UNSUPPORTED_GGUF;
    }

    if (options != nullptr && options->ContextLength != 0)
    {
        model->ContextLength = options->ContextLength;
    }

    model->Context = std::make_unique<SockJackDmlContext>();
    HRESULT hr = model->Context->Initialize();
    if (FAILED(hr))
    {
        error = FormatHResult("SockJackDml DirectML context initialization", hr);
        model->Context.reset();
        FillModelInfo(info, *model, SOCKJACKDML_DIRECTML_UNAVAILABLE, error.c_str());
        SetLastErrorMessage(error.c_str());
        return SOCKJACKDML_DIRECTML_UNAVAILABLE;
    }

    const char* message = model->UnsupportedTensorCount == 0
        ? "GGUF metadata and tensor table loaded natively; DirectML device is ready."
        : "GGUF loaded natively; unsupported tensor types are present and decode will fail closed until their operators are implemented.";
    FillModelInfo(info, *model, SOCKJACKDML_OK, message);
    *modelHandle = model.release();
    SetLastErrorMessage("");
    return SOCKJACKDML_OK;
}

std::uint32_t SockJackDmlRunTensorIdentityFloat32(
    void* modelHandle,
    const SockJackDmlTensorDispatchOptions* options,
    SockJackDmlTensorDispatchResult* result)
{
    if (modelHandle == nullptr || result == nullptr || result->Size < sizeof(SockJackDmlTensorDispatchResult))
    {
        SetLastErrorMessage("SockJackDmlRunTensorIdentityFloat32 requires a loaded model handle and result with Size initialized.");
        return SOCKJACKDML_INVALID_ARGUMENT;
    }

    if (options != nullptr && options->Size < sizeof(SockJackDmlTensorDispatchOptions))
    {
        SetLastErrorMessage("SockJackDmlRunTensorIdentityFloat32 options Size is invalid.");
        return SOCKJACKDML_INVALID_ARGUMENT;
    }

    const std::uint32_t resultSize = result->Size;
    std::memset(result, 0, sizeof(SockJackDmlTensorDispatchResult));
    result->Size = resultSize;
    result->Status = SOCKJACKDML_INTERNAL_ERROR;

    SockJackDmlModel* model = static_cast<SockJackDmlModel*>(modelHandle);
    const char* requestedName = options == nullptr ? nullptr : options->TensorName;
    const GgufTensorInfo* tensor = FindFloat32Tensor(*model, requestedName);
    if (tensor == nullptr)
    {
        const char* message = requestedName != nullptr && requestedName[0] != '\0'
            ? "Requested F32 GGUF tensor was not found."
            : "No F32 GGUF tensor is available for the native tensor identity smoke dispatch.";
        result->Status = SOCKJACKDML_UNSUPPORTED_GGUF;
        CopyMessage(result->Message, std::size(result->Message), message);
        SetLastErrorMessage(message);
        return SOCKJACKDML_UNSUPPORTED_GGUF;
    }

    std::uint64_t tensorElementCount = 0;
    std::uint64_t tensorByteCount = 0;
    std::string error;
    if (!TryCalculateTensorByteCount(*tensor, tensorElementCount, tensorByteCount, error))
    {
        result->Status = SOCKJACKDML_UNSUPPORTED_GGUF;
        CopyString(result->TensorName, std::size(result->TensorName), tensor->Name);
        CopyString(result->Message, std::size(result->Message), error);
        SetLastErrorMessage(error.c_str());
        return SOCKJACKDML_UNSUPPORTED_GGUF;
    }

    if (tensor->Offset > model->Mapping.Size - model->DataOffset ||
        tensorByteCount > model->Mapping.Size - model->DataOffset - tensor->Offset)
    {
        error = "GGUF F32 tensor payload exceeds mapped model file bounds.";
        result->Status = SOCKJACKDML_UNSUPPORTED_GGUF;
        CopyString(result->TensorName, std::size(result->TensorName), tensor->Name);
        CopyString(result->Message, std::size(result->Message), error);
        SetLastErrorMessage(error.c_str());
        return SOCKJACKDML_UNSUPPORTED_GGUF;
    }

    const std::uint32_t requestedMax = options == nullptr || options->MaxElementCount == 0 ? 1024 : options->MaxElementCount;
    const std::uint32_t dispatchElementCount = static_cast<std::uint32_t>(
        (std::min)(tensorElementCount, static_cast<std::uint64_t>(requestedMax)));
    if (dispatchElementCount == 0)
    {
        SetLastErrorMessage("SockJackDmlRunTensorIdentityFloat32 requires at least one element to dispatch.");
        return SOCKJACKDML_INVALID_ARGUMENT;
    }

    const std::uint64_t absoluteOffset = model->DataOffset + tensor->Offset;
    const float* source = reinterpret_cast<const float*>(model->Mapping.View + absoluteOffset);
    std::vector<float> output(dispatchElementCount);
    HRESULT hr = RunIdentityFloat32Dispatch(source, output.data(), dispatchElementCount);
    if (FAILED(hr))
    {
        error = FormatHResult("SockJackDmlRunTensorIdentityFloat32", hr);
        result->Status = SOCKJACKDML_DIRECTML_UNAVAILABLE;
        CopyString(result->TensorName, std::size(result->TensorName), tensor->Name);
        CopyString(result->Message, std::size(result->Message), error);
        SetLastErrorMessage(error.c_str());
        return SOCKJACKDML_DIRECTML_UNAVAILABLE;
    }

    result->Status = SOCKJACKDML_OK;
    result->TensorElementCount = tensorElementCount;
    result->DispatchedElementCount = dispatchElementCount;
    result->TensorByteCount = tensorByteCount;
    result->TensorType = tensor->Type;
    result->DimensionCount = static_cast<std::uint32_t>(tensor->Dimensions.size());
    for (size_t index = 0; index < tensor->Dimensions.size() && index < std::size(result->Dimensions); index++)
    {
        result->Dimensions[index] = tensor->Dimensions[index];
    }

    result->PreviewCount = (std::min)(static_cast<std::uint32_t>(std::size(result->InputPreview)), dispatchElementCount);
    for (std::uint32_t index = 0; index < result->PreviewCount; index++)
    {
        result->InputPreview[index] = source[index];
        result->OutputPreview[index] = output[index];
    }

    CopyString(result->TensorName, std::size(result->TensorName), tensor->Name);
    CopyMessage(result->Message, std::size(result->Message), "F32 GGUF tensor payload slice dispatched through DirectML identity and read back successfully.");
    SetLastErrorMessage("");
    return SOCKJACKDML_OK;
}

std::uint32_t SockJackDmlUnloadModel(void* modelHandle)
{
    if (modelHandle == nullptr)
    {
        return SOCKJACKDML_OK;
    }

    delete static_cast<SockJackDmlModel*>(modelHandle);
    SetLastErrorMessage("");
    return SOCKJACKDML_OK;
}

const char* SockJackDmlGetLastError()
{
    std::lock_guard<std::mutex> guard(g_errorLock);
    return g_lastError.c_str();
}

