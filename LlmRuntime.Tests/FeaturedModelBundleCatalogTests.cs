using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class FeaturedModelBundleCatalogTests
{
    [TestMethod]
    public void PictureBankInstalled_RequiresEveryPinnedRepositoryManifest()
    {
        string root = CreateTemporaryRoot();
        try
        {
            Assert.IsFalse(PictureBankAiBundleCatalog.IsInstalled(root));
            foreach (PictureBankRepositoryPin pin in PictureBankAiBundleCatalog.Repositories)
            {
                string directory = Path.Combine(root, PictureBankAiBundleCatalog.TargetDirectory(pin.Repository).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
                {
                    metadata = new
                    {
                        source = "huggingface:" + pin.Repository,
                        revision = pin.Revision,
                        pictureBankBundleId = PictureBankAiBundleCatalog.BundleId
                    }
                }));
            }

            Assert.IsTrue(PictureBankAiBundleCatalog.IsInstalled(root));
            Assert.AreEqual(
                Path.Combine(root, "PictureBank", PictureBankAiBundleCatalog.BundleId),
                PictureBankAiBundleCatalog.BundleDirectory(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void HeirowSongInstalled_RequiresVerifiedBundleAndAllPinnedParts()
    {
        string root = CreateTemporaryRoot();
        try
        {
            foreach (HeirowSongRepositoryPin pin in HeirowSongModelBundleCatalog.Repositories)
            {
                string directory = Path.Combine(root, HeirowSongModelBundleCatalog.TargetDirectory(pin.Repository).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "manifest.json"), "{}");
            }

            string bundleDirectory = HeirowSongModelBundleCatalog.BundleDirectory(root);
            Directory.CreateDirectory(bundleDirectory);
            File.WriteAllText(Path.Combine(bundleDirectory, "manifest.json"), JsonSerializer.Serialize(new
            {
                bundleId = HeirowSongModelBundleCatalog.BundleId,
                verified = true
            }));

            Assert.IsTrue(HeirowSongModelBundleCatalog.IsInstalled(root));
            File.WriteAllText(Path.Combine(bundleDirectory, "manifest.json"), JsonSerializer.Serialize(new
            {
                bundleId = HeirowSongModelBundleCatalog.BundleId,
                verified = false
            }));
            Assert.IsFalse(HeirowSongModelBundleCatalog.IsInstalled(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "heirowllm-featured-bundle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
