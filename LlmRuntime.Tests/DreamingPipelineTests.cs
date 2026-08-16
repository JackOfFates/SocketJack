using System.Text.Json;
using LmVs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class DreamingPipelineTests
{
    [TestMethod]
    public void DreamSourceDistinguishesReadableEmptyUnreadableAndOwnerScopedSessions()
    {
        string root = TempRoot();
        try
        {
            using var proxy = CreateProxy(root);
            proxy.SaveDreamSessionForDiagnostics("owner-a", "[{\"role\":\"user\",\"content\":\"I prefer cedar pencils.\"}]", "hero-model");
            proxy.SaveDreamSessionForDiagnostics("owner-a", "[]");
            proxy.SaveDreamSessionForDiagnostics("owner-a", "[{\"role\":\"user\",\"content\":\"unreadable\"}]", corruptProtectedPayload: true);
            proxy.SaveDreamSessionForDiagnostics("owner-b", "[{\"role\":\"user\",\"content\":\"other owner\"}]");

            DreamSourceDiagnosticsSnapshot source = proxy.GetDreamSourceDiagnostics("owner-a");

            Assert.AreEqual(3, source.EligibleSessions);
            Assert.AreEqual(1, source.ReadableSessions);
            Assert.AreEqual(1, source.EmptySessions);
            Assert.AreEqual(1, source.UnavailableSessions);
            Assert.AreEqual(1, source.ProcessedMessages);
            Assert.AreEqual("source-read", source.FailureStage);
            Assert.AreEqual("hero-model", source.SelectedModel);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SavedChatCompletesDreamAndHeroAlignmentWithConfiguredService()
    {
        string root = TempRoot();
        try
        {
            using var proxy = CreateProxy(root);
            proxy.SaveDreamSessionForDiagnostics("owner-a", "[{\"role\":\"user\",\"content\":\"I prefer cedar pencils for sketching.\"},{\"role\":\"assistant\",\"content\":\"Noted.\"}]", "selected-hero-model");
            DreamSettingsSnapshot settings = proxy.GetDreamSettingsDiagnostics("owner-a");
            settings.Enabled = true; settings.Preset = "custom"; settings.Model = "dream-model"; settings.Service = "agent";
            proxy.SaveDreamSettingsDiagnostics("owner-a", settings);
            proxy.DreamReflectionOverrideForDiagnostics = _ => "{\"summary\":\"Preference reviewed.\",\"candidates\":[]}";
            proxy.AlignmentAssessmentOverrideForDiagnostics = _ => new AlignmentAssessmentSnapshot
            {
                Category = "constructive", Confidence = .98, Delta = 2, Reason = "A durable preference was handled constructively.",
                CharacterTraits = new Dictionary<string, int> { ["Nobility"] = 8 }
            };

            await proxy.RunDreamNowDiagnosticsAsync("owner-a");

            DreamStatusSnapshot status = proxy.GetDreamStatusDiagnostics("owner-a");
            DreamJournalSnapshot journal = proxy.GetDreamJournalDiagnostics("owner-a").First();
            AlignmentSnapshot alignment = proxy.GetAlignmentSnapshot("owner-a");
            Assert.AreEqual("completed", status.Status);
            Assert.AreEqual(1, status.ProcessedSessions);
            Assert.AreEqual(2, status.ProcessedMessages);
            Assert.AreEqual("agent", status.ResolvedService);
            Assert.AreEqual("completed", journal.ChecksAndBalancesStatus);
            Assert.AreEqual("selected-hero-model", journal.ChecksAndBalancesModel);
            Assert.AreEqual("agent", journal.DreamService);
            Assert.AreEqual(2, alignment.Score);
            Assert.AreEqual(journal.Id, alignment.ChecksAndBalancesDreamId);
            Assert.AreEqual(16, alignment.CharacterTraits.Count);
            Assert.AreEqual(0, proxy.GetDreamSourceDiagnostics("owner-a").ProcessedMessages, "Successful alignment must commit the source checkpoint.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task AlignmentFailureRetainsReflectionAndRetriesWithoutConsumingSource()
    {
        string root = TempRoot();
        try
        {
            using var proxy = CreateProxy(root);
            proxy.SaveDreamSessionForDiagnostics("owner-a", "[{\"role\":\"user\",\"content\":\"Remember that I prefer blue notebooks.\"}]", "hero-model");
            proxy.DreamReflectionOverrideForDiagnostics = _ => "{\"summary\":\"Preference reviewed.\",\"candidates\":[]}";
            proxy.AlignmentAssessmentOverrideForDiagnostics = _ => null!;

            await proxy.RunDreamNowDiagnosticsAsync("owner-a");
            DreamJournalSnapshot pending = proxy.GetDreamJournalDiagnostics("owner-a").First();
            Assert.AreEqual("alignment-retry", pending.Status);
            Assert.AreEqual("retry-pending", pending.ChecksAndBalancesStatus);
            Assert.AreEqual(1, pending.ChecksAndBalancesRetryCount);
            Assert.AreEqual(1, proxy.GetDreamSourceDiagnostics("owner-a").ProcessedMessages, "Failed alignment must not advance the checkpoint.");

            proxy.AlignmentAssessmentOverrideForDiagnostics = _ => new AlignmentAssessmentSnapshot { Category = "neutral", Confidence = .98, Delta = 0, CharacterTraits = new Dictionary<string, int> { ["Honesty"] = 7 } };
            await proxy.RunDreamNowDiagnosticsAsync("owner-a");
            DreamJournalSnapshot completed = proxy.GetDreamJournalDiagnostics("owner-a").First();
            Assert.AreEqual("completed", completed.Status);
            Assert.AreEqual("completed", completed.ChecksAndBalancesStatus);
            Assert.AreEqual(1, proxy.GetDreamJournalDiagnostics("owner-a").Count, "Retry must reuse the existing Dream journal entry.");
            Assert.AreEqual(0, proxy.GetDreamSourceDiagnostics("owner-a").ProcessedMessages);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LegacyZeroMessageCheckpointMigratesToOneTimeBackfillWithBackup()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        string statePath = Path.Combine(root, "dream-state.json");
        File.WriteAllText(statePath, "[{\"ownerKey\":\"owner-a\",\"hasOverride\":true,\"settings\":{\"enabled\":true},\"status\":\"completed\",\"processedSessionUtc\":{\"sess_old\":\"2026-08-01T00:00:00Z\"},\"journal\":[]}]");
        try
        {
            using (var proxy = CreateProxy(root))
            {
                DreamStatusSnapshot status = proxy.GetDreamStatusDiagnostics("owner-a");
                Assert.AreEqual(2, status.CheckpointVersion);
                Assert.IsTrue(status.BackfillPending);
            }
            Assert.IsTrue(File.Exists(statePath + ".pre-v2.bak"));
            using var reloaded = CreateProxy(root);
            Assert.IsTrue(reloaded.GetDreamStatusDiagnostics("owner-a").BackfillPending);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ResourceHysteresisStartsBelowStartThresholdAndPausesAbovePauseThreshold()
    {
        string root = TempRoot();
        try
        {
            using var proxy = CreateProxy(root);
            DreamSettingsSnapshot settings = proxy.GetDreamSettingsDiagnostics("owner-a");
            settings.Preset = "custom"; settings.StartCpuPercent = 35; settings.PauseCpuPercent = 65;
            proxy.SaveDreamSettingsDiagnostics("owner-a", settings);
            Assert.AreEqual("", proxy.GetDreamPressureDiagnostics("owner-a", new DreamResourceSnapshot { CpuPercent = 30 }, running: false));
            Assert.AreEqual("cpu", proxy.GetDreamPressureDiagnostics("owner-a", new DreamResourceSnapshot { CpuPercent = 40 }, running: false));
            Assert.AreEqual("", proxy.GetDreamPressureDiagnostics("owner-a", new DreamResourceSnapshot { CpuPercent = 60 }, running: true));
            Assert.AreEqual("cpu", proxy.GetDreamPressureDiagnostics("owner-a", new DreamResourceSnapshot { CpuPercent = 70 }, running: true));
            Assert.AreEqual("foreground-model-work", proxy.GetDreamPressureDiagnostics("owner-a", new DreamResourceSnapshot { ForegroundModelWork = true }, running: true));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ResourceSamplingSubtractsWorkstationPrivateMemoryFromOutsidePressure()
    {
        double percent = LmVsProxy.CalculateOutsideDreamRamPercent(16UL << 30, 15UL << 30, 6UL << 30, 93.75);

        Assert.AreEqual(56.25, percent, 0.01);
        Assert.AreEqual(75, LmVsProxy.CalculateOutsideDreamRamPercent(0, 0, 0, 75), 0.01);
    }

    [TestMethod]
    public void AlignmentParserAcceptsStringOrArrayTextFields()
    {
        using JsonDocument document = JsonDocument.Parse("""{"category":["neutral"],"reason":["First sentence.","Second sentence."]}""");

        Assert.AreEqual("neutral", LmVsProxy.ReadAlignmentTextProperty(document.RootElement, "category", "fallback"));
        Assert.AreEqual("First sentence. Second sentence.", LmVsProxy.ReadAlignmentTextProperty(document.RootElement, "reason", "fallback", joinArray: true));
    }

    [TestMethod]
    public void AlignmentDreamEvidenceKeepsDreamSummaryAndBoundsTranscript()
    {
        string source = "<completed-dream>\nReflection: useful reflection\nCandidates: useful candidate\nSource transcript:\n" + new string('x', 5000) + "\nfinal evidence</completed-dream>";

        string bounded = LmVsProxy.BuildBoundedAlignmentDreamEvidence(source);

        Assert.IsTrue(bounded.Length <= 2400);
        StringAssert.Contains(bounded, "Reflection: useful reflection");
        StringAssert.Contains(bounded, "Candidates: useful candidate");
        StringAssert.Contains(bounded, "Source transcript:");
        StringAssert.Contains(bounded, "final evidence</completed-dream>");
    }

    [DataTestMethod]
    [DataRow(0.87, 0.87)]
    [DataRow(9.0, 0.9)]
    [DataRow(42.0, 1.0)]
    public void AlignmentConfidenceAcceptsDecimalOrTenPointScale(double input, double expected)
    {
        Assert.AreEqual(expected, LmVsProxy.NormalizeAlignmentConfidence(input), 0.001);
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "jackllm-dream-pipeline-tests", Guid.NewGuid().ToString("N"));
    private static LmVsProxy CreateProxy(string root) => new("127.0.0.1", 1234, 21434, 21436, new LmVsProxyStorageOptions { ChatDataRoot = root });
}
