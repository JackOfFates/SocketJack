using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmBackendAutoSelectorTests
{
    [TestMethod]
    public void BuildCudaProbeFromNvidiaSmiOutput_MarksTitanXCompute52LegacyForAuto()
    {
        LlmBackendAutoSelector.CudaAutoBackendProbe probe =
            LlmBackendAutoSelector.BuildCudaProbeFromNvidiaSmiOutput("NVIDIA GeForce GTX TITAN X, 5.2");

        Assert.IsTrue(probe.DriverAvailable);
        Assert.IsFalse(probe.SupportedForAuto);
        StringAssert.Contains(probe.UnsupportedReason, "below the CUDA12 auto minimum");
        StringAssert.Contains(probe.UnsupportedReason, "NVIDIA GeForce GTX TITAN X compute 5.2");
    }

    [TestMethod]
    public void ResolveAutoBackend_UsesCudaWhenCudaDeviceIsLegacy()
    {
        var cuda = new LlmBackendAutoSelector.CudaAutoBackendProbe(
            DriverAvailable: true,
            SupportedForAuto: false,
            UnsupportedReason: "old CUDA device");

        LlmBackendKind backend = LlmBackendAutoSelector.ResolveAutoBackend(
            cuda,
            hasCudaAsset: true,
            hasVulkanBackend: true,
            requireGpu: true);

        Assert.AreEqual(LlmBackendKind.Cuda12, backend);
    }

    [TestMethod]
    public void ResolveAutoBackend_KeepsCudaWhenSupported()
    {
        var cuda = new LlmBackendAutoSelector.CudaAutoBackendProbe(
            DriverAvailable: true,
            SupportedForAuto: true,
            UnsupportedReason: "");

        LlmBackendKind backend = LlmBackendAutoSelector.ResolveAutoBackend(
            cuda,
            hasCudaAsset: true,
            hasVulkanBackend: true,
            requireGpu: true);

        Assert.AreEqual(LlmBackendKind.Cuda12, backend);
    }

    [TestMethod]
    public void ResolveAutoBackend_UsesVulkanWhenCudaIsUnavailable()
    {
        LlmBackendKind backend = LlmBackendAutoSelector.ResolveAutoBackend(
            LlmBackendAutoSelector.CudaAutoBackendProbe.Unavailable,
            hasCudaAsset: true,
            hasVulkanBackend: true,
            requireGpu: true);

        Assert.AreEqual(LlmBackendKind.Vulkan, backend);
    }
}
