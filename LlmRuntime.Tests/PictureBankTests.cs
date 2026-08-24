using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using heirowLLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using NetHttpClient = System.Net.Http.HttpClient;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class PictureBankTests
{
    [TestMethod]
    public void ChickenChaserShowsDownloadedInventoryButDisablesNonChatModels()
    {
        string source = JsonSerializer.Serialize(new
        {
            selected = "ACE-Step-acestep-5Hz-lm-0.6B-pytorch",
            models = new object[]
            {
                new { id = "Qwen-text-GGUF", disabled = false, supportsImageGeneration = false, supportsAudioGeneration = false, supportsVideoGeneration = false },
                new { id = "ACE-Step-acestep-5Hz-lm-0.6B-pytorch", disabled = false, supportsImageGeneration = false, supportsAudioGeneration = true, supportsVideoGeneration = false },
                new { id = "manifest.json", disabled = false, supportsImageGeneration = false, supportsAudioGeneration = true, supportsVideoGeneration = false }
            }
        });

        using JsonDocument document = JsonDocument.Parse(HeirowLlm.BuildChickenChaserModelInventory(source));
        JsonElement root = document.RootElement;
        Assert.AreEqual("auto", root.GetProperty("selected").GetString());
        JsonElement models = root.GetProperty("models");
        Assert.AreEqual(2, models.GetArrayLength());
        Assert.IsTrue(models[0].GetProperty("chickenChaserCompatible").GetBoolean());
        Assert.IsFalse(models[0].GetProperty("disabled").GetBoolean());
        Assert.IsFalse(models[1].GetProperty("chickenChaserCompatible").GetBoolean());
        Assert.IsTrue(models[1].GetProperty("disabled").GetBoolean());
        StringAssert.Contains(models[1].GetProperty("status").GetString(), "unavailable");
    }

    [TestMethod]
    public void ChickenChaserReplyPreservesMarkdownNewlines()
    {
        const string reply = "## Safe bets\r\n\r\n- Potato salad\r\n- Garlic bread\r\n\r\nWhat sounds good?";

        string rendered = HeirowLlm.PreserveChickenChaserMarkdown(reply, 4000);

        Assert.AreEqual("## Safe bets\n\n- Potato salad\n- Garlic bread\n\nWhat sounds good?", rendered);
        Assert.AreEqual("## Safe…", HeirowLlm.PreserveChickenChaserMarkdown("## Safe bets", 8));
    }

    [TestMethod]
    public void CoreEditorCommandsPersistColorsLayersBlendsLocksAndBrushStrokes()
    {
        string root = Path.Combine(Path.GetTempPath(), "picturebank-core-editor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new PictureBankService(root, null);
            PictureBankDocument document = service.Create(new() { Title = "Core tools" }, "webauth:owner");
            PictureBankDocument edited = service.Apply(new()
            {
                DocumentId = document.Id,
                BaseRevision = document.Revision,
                Label = "Core tool batch",
                Commands = new()
                {
                    new() { Type = "setDocumentColors", Arguments = new() { ["foreground"] = "#ff3366", ["background"] = "#102030" } },
                    new() { Type = "addLayer", Arguments = new() { ["name"] = "Paint", ["layerType"] = "raster" } }
                }
            }, "webauth:owner");
            string paintId = edited.SelectedLayerId;
            edited = service.Apply(new()
            {
                DocumentId = edited.Id,
                BaseRevision = edited.Revision,
                Label = "Paint and layer properties",
                Commands = new()
                {
                    new() { Type = "appendBrushStroke", LayerId = paintId, Arguments = new() { ["x"] = "45", ["y"] = "60", ["size"] = "24", ["opacity"] = ".75", ["color"] = "#ff3366" } },
                    new() { Type = "setBlendMode", LayerId = paintId, Arguments = new() { ["blendMode"] = "multiply" } },
                    new() { Type = "setLocked", LayerId = paintId, Arguments = new() { ["locked"] = "true" } }
                }
            }, "webauth:owner");

            PictureBankLayer paint = edited.Layers.Single(layer => layer.Id == paintId);
            Assert.AreEqual("#ff3366", edited.ForegroundColor);
            Assert.AreEqual("#102030", edited.BackgroundColor);
            Assert.AreEqual("multiply", paint.BlendMode);
            Assert.IsTrue(paint.Locked);
            StringAssert.Contains(paint.Properties["strokes"], "\"X\":45");
            Assert.ThrowsException<InvalidOperationException>(() => service.Apply(new()
            {
                DocumentId = edited.Id,
                BaseRevision = edited.Revision,
                Commands = new() { new() { Type = "setOpacity", LayerId = paintId, Arguments = new() { ["opacity"] = ".5" } } }
            }, "webauth:owner"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ImportedRasterLayersMoveEraseAndRetainSelectionClip()
    {
        string root = Path.Combine(Path.GetTempPath(), "picturebank-raster-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new PictureBankService(root, null);
            PictureBankDocument document = service.Create(new() { Title = "Raster tools", Width = 320, Height = 240 }, "webauth:owner");
            const string onePixelPng = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
            PictureBankDocument imported = service.Apply(new()
            {
                DocumentId = document.Id,
                BaseRevision = document.Revision,
                Label = "Import and move",
                Commands = new()
                {
                    new() { Type = "importImage", Arguments = new() { ["name"] = "Portrait", ["sourceName"] = "portrait.png", ["mimeType"] = "image/png", ["content"] = onePixelPng, ["width"] = "1000", ["height"] = "2000" } }
                }
            }, "webauth:owner");
            string rasterId = imported.SelectedLayerId;
            PictureBankLayer fitted = imported.Layers.Single(layer => layer.Id == rasterId);
            Assert.AreEqual("1000", fitted.Properties["sourceWidth"]);
            Assert.AreEqual("2000", fitted.Properties["sourceHeight"]);
            Assert.AreEqual("120", fitted.Properties["width"]);
            Assert.AreEqual("240", fitted.Properties["height"]);
            Assert.AreEqual("true", fitted.Properties["aspectRatioLocked"]);
            imported = service.Apply(new()
            {
                DocumentId = imported.Id,
                BaseRevision = imported.Revision,
                Label = "Move and erase selection",
                Commands = new()
                {
                    new() { Type = "translateLayer", LayerId = rasterId, Arguments = new() { ["deltaX"] = "12.5", ["deltaY"] = "-4" } },
                    new() { Type = "resizeLayer", LayerId = rasterId, Arguments = new() { ["width"] = "60", ["height"] = "240", ["preserveAspect"] = "true", ["widthChanged"] = "true" } },
                    new() { Type = "rotateLayer", LayerId = rasterId, Arguments = new() { ["degrees"] = "450" } },
                    new() { Type = "flipLayer", LayerId = rasterId, Arguments = new() { ["axis"] = "horizontal" } },
                    new() { Type = "setLayerColor", LayerId = rasterId, Arguments = new() { ["brightness"] = "1.2", ["hue"] = "45", ["saturation"] = ".8" } },
                    new() { Type = "appendBrushStroke", LayerId = rasterId, Arguments = new() { ["x"] = "20", ["y"] = "30", ["size"] = "12", ["mode"] = "erase", ["clip"] = "{\"x\":10,\"y\":10,\"width\":40,\"height\":30}" } }
                }
            }, "webauth:owner");

            PictureBankLayer raster = imported.Layers.Single(layer => layer.Id == rasterId);
            Assert.AreEqual("12.5", raster.Properties["offsetX"]);
            Assert.AreEqual("-4", raster.Properties["offsetY"]);
            Assert.AreEqual("60", raster.Properties["width"]);
            Assert.AreEqual("120", raster.Properties["height"]);
            Assert.AreEqual("90", raster.Properties["rotation"]);
            Assert.AreEqual("true", raster.Properties["flipX"]);
            Assert.AreEqual("1.2", raster.Properties["brightness"]);
            Assert.AreEqual("45", raster.Properties["hue"]);
            Assert.AreEqual("0.8", raster.Properties["saturation"]);
            StringAssert.Contains(raster.Properties["strokes"], "\"Erase\":true");
            StringAssert.Contains(raster.Properties["strokes"], "\"Width\":40");

            PictureBankDocument vector = service.Apply(new()
            {
                DocumentId = imported.Id,
                BaseRevision = imported.Revision,
                Commands = new() { new() { Type = "addLayer", Arguments = new() { ["name"] = "Vector", ["layerType"] = "shape" } } }
            }, "webauth:owner");
            Assert.ThrowsException<InvalidOperationException>(() => service.Apply(new()
            {
                DocumentId = vector.Id,
                BaseRevision = vector.Revision,
                Commands = new() { new() { Type = "appendBrushStroke", LayerId = vector.SelectedLayerId, Arguments = new() { ["mode"] = "erase" } } }
            }, "webauth:owner"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CommandsAreOwnerScopedRevisionedAndPreviewApprovalsAreSingleUse()
    {
        string root = Path.Combine(Path.GetTempPath(), "picturebank-service-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new PictureBankService(root, null);
            PictureBankDocument document = service.Create(new() { Title = "Owner A artwork", Width = 800, Height = 600 }, "webauth:owner-a");
            Assert.AreEqual(1, document.Revision);
            Assert.ThrowsException<FileNotFoundException>(() => service.Get(document.Id, "webauth:owner-b"));

            PictureBankDocument edited = service.Apply(new()
            {
                DocumentId = document.Id, BaseRevision = document.Revision, Label = "Add text",
                Commands = new() { new() { Type = "addLayer", Arguments = new() { ["name"] = "Headline", ["layerType"] = "text", ["prompt"] = "Hello" } } }
            }, "webauth:owner-a");
            Assert.AreEqual(2, edited.Revision);
            Assert.AreEqual("Headline", edited.Layers.Last().Name);
            Assert.ThrowsException<InvalidOperationException>(() => service.Apply(new() { DocumentId = document.Id, BaseRevision = 1 }, "webauth:owner-a"));

            PictureBankAgentPlan plan = service.CreatePlan(new() { DocumentId = document.Id, BaseRevision = 2, Prompt = "hide this layer", SelectedLayerId = edited.SelectedLayerId }, "webauth:owner-a", new { ok = true });
            PictureBankDocument applied = service.ApplyPlan(plan.ApprovalToken, "webauth:owner-a");
            Assert.AreEqual(3, applied.Revision);
            Assert.IsFalse(applied.Layers.Single(layer => layer.Id == edited.SelectedLayerId).Visible);
            Assert.ThrowsException<UnauthorizedAccessException>(() => service.ApplyPlan(plan.ApprovalToken, "webauth:owner-a"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PictureBankIsAuthenticatedAndExposesPresetDrivenApplication()
    {
        string root = Path.Combine(Path.GetTempPath(), "picturebank-endpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var proxy = new HeirowLlm("127.0.0.1", NextPort(), NextPort(), NextPort(), root) { PublicAccessEnabled = false };
            Assert.IsTrue(proxy.ChatServer.Listen());
            using var client = new NetHttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{proxy.ChatServerPort}/"), Timeout = TimeSpan.FromSeconds(15) };
            using HttpResponseMessage anonymous = await client.GetAsync("/PictureBank");
            Assert.AreEqual(HttpStatusCode.Forbidden, anonymous.StatusCode);
            StringAssert.Contains(await anonymous.Content.ReadAsStringAsync(), "sign-in required");
            using HttpResponseMessage anonymousChicken = await client.GetAsync("/api/chickenchaser/settings");
            Assert.AreEqual(HttpStatusCode.Forbidden, anonymousChicken.StatusCode);

            using var desktopClient = new NetHttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = client.BaseAddress };
            desktopClient.DefaultRequestHeaders.Add("X-Chicken-Chaser-Key", proxy.ChickenChaserApiKey);
            using HttpResponseMessage legacyDesktopKey = await desktopClient.GetAsync("/api/chickenchaser/settings");
            Assert.AreEqual(HttpStatusCode.Forbidden, legacyDesktopKey.StatusCode, "The internal Chicken Chaser key must not bypass Workstation account login.");

            await Authenticate(proxy, client);
            string desktopSettings = await client.GetStringAsync("/api/chickenchaser/settings");
            using (JsonDocument desktopJson = JsonDocument.Parse(desktopSettings)) Assert.AreEqual("chat", desktopJson.RootElement.GetProperty("settings").GetProperty("mode").GetString());
            using HttpResponseMessage savedMemory = await client.PostAsync("/api/chat-memories", new StringContent(JsonSerializer.Serialize(new { action = "add", text = "My girlfriends name is Meghan.", topic = "General" }), Encoding.UTF8, "application/json"));
            savedMemory.EnsureSuccessStatusCode();
            using HttpResponseMessage unrelatedMemory = await client.PostAsync("/api/chat-memories", new StringContent(JsonSerializer.Serialize(new { action = "add", text = "The user's favorite car is an Audi A3.", topic = "Vehicles" }), Encoding.UTF8, "application/json"));
            unrelatedMemory.EnsureSuccessStatusCode();
            using HttpResponseMessage recalledMemory = await client.PostAsync("/api/chickenchaser/memories/recall", new StringContent(JsonSerializer.Serialize(new { query = "girlfriend", take = 4 }), Encoding.UTF8, "application/json"));
            recalledMemory.EnsureSuccessStatusCode();
            using (JsonDocument recallJson = JsonDocument.Parse(await recalledMemory.Content.ReadAsStringAsync()))
            {
                Assert.IsTrue(recallJson.RootElement.GetProperty("ok").GetBoolean());
                JsonElement memories = recallJson.RootElement.GetProperty("memories");
                Assert.IsTrue(memories.GetArrayLength() > 0);
                StringAssert.Contains(memories[0].GetProperty("text").GetString(), "Meghan");
                Assert.IsFalse(memories.EnumerateArray().Any(memory => (memory.GetProperty("text").GetString() ?? "").Contains("Audi", StringComparison.OrdinalIgnoreCase)));
            }
            using HttpResponseMessage unmatchedRecall = await client.PostAsync("/api/chickenchaser/memories/recall", new StringContent(JsonSerializer.Serialize(new { query = "quantum chromodynamics", take = 4 }), Encoding.UTF8, "application/json"));
            unmatchedRecall.EnsureSuccessStatusCode();
            using (JsonDocument unmatchedJson = JsonDocument.Parse(await unmatchedRecall.Content.ReadAsStringAsync()))
                Assert.AreEqual(0, unmatchedJson.RootElement.GetProperty("count").GetInt32());
            MethodInfo memoryHintMethod = typeof(HeirowLlm).GetMethod("BuildChatMemorySystemHint", BindingFlags.NonPublic | BindingFlags.Instance)!;
            string matchedHint = (string)memoryHintMethod.Invoke(proxy, new object[] { "webauth:picturebank-test", "What is my girlfriend's name?" })!;
            StringAssert.Contains(matchedHint, "Meghan");
            Assert.IsFalse(matchedHint.Contains("Audi", StringComparison.OrdinalIgnoreCase));
            string unmatchedHint = (string)memoryHintMethod.Invoke(proxy, new object[] { "webauth:picturebank-test", "Explain quantum chromodynamics." })!;
            Assert.AreEqual("", unmatchedHint);
            string page = await client.GetStringAsync("/PictureBank");
            StringAssert.Contains(page, "PictureBank");
            StringAssert.Contains(page, "/api/picturebank/application");
            StringAssert.Contains(page, "id=\"foregroundColor\"");
            StringAssert.Contains(page, "Photoshop feature parity");
            StringAssert.Contains(page, "context-menu");
            StringAssert.Contains(page, "label:'Transform'");
            StringAssert.Contains(page, "label:'Resize…'");
            StringAssert.Contains(page, "label:'Rotate 90° clockwise'");
            StringAssert.Contains(page, "label:'Color'");
            StringAssert.Contains(page, "Brightness / Hue / Saturation…");
            StringAssert.Contains(page, "type:'resizeDocument'");
            StringAssert.Contains(page, "pictureBankChickenChaserPreview");
            string application = await client.GetStringAsync("/api/picturebank/application");
            StringAssert.Contains(application, "\"slug\":\"picturebank\"");
            StringAssert.Contains(application, "\"type\":\"picturebank-studio\"");
            string chat = await client.GetStringAsync("/");
            StringAssert.Contains(chat, "id=\"workspacePictureBankTab\"");
            StringAssert.Contains(chat, "data-workspace-view=\"picturebank\"");
            StringAssert.Contains(chat, "id=\"hardwareIoWriteFill\"");
            StringAssert.Contains(chat, "hardware-rgb-change");
            StringAssert.Contains(chat, "id=\"chickenChaserShell\"");
            StringAssert.Contains(chat, "id=\"chickenModeToggle\"");
            StringAssert.Contains(chat, "id=\"chickenSettingModel\"><option");
            StringAssert.Contains(chat, "id=\"chickenResetPosition\"");
            StringAssert.Contains(chat, "data-ui-revision=\"chicken-chaser-20260822-17\"");
            StringAssert.Contains(chat, "id=\"chickenOpenInHeirowLlm\"");
            StringAssert.Contains(chat, "window.openChickenCurrentSessionInHeirowLlm=openChickenCurrentSessionInHeirowLlm");
            StringAssert.Contains(chat, "card.addEventListener('mouseenter',pauseLifetime)");
            StringAssert.Contains(chat, "lifetimeTimer=window.setTimeout(dismiss");
            StringAssert.Contains(chat, "activeNotices:new Map()");
            StringAssert.Contains(chat, "count.textContent='x'+repetitions");
            StringAssert.Contains(chat, "Math.cos(Math.PI*progress)");
            StringAssert.Contains(chat, ".chicken-notifications.above");
            StringAssert.Contains(chat, "function positionChickenNotifications()");
            StringAssert.Contains(chat, ".chicken-notifications.down");
            StringAssert.Contains(chat, "upwardAnchor-totalHeight<8");
            StringAssert.Contains(chat, "Math.max(2600,Math.min(6000,1800+text.length*14))");
            StringAssert.Contains(chat, "chicken-chaser-shell.offline");
            StringAssert.Contains(chat, "queueChickenChaserNotice('heirowLLM hint'");
            StringAssert.Contains(chat, "picturebank-preview");
            using HttpResponseMessage savedChicken = await client.PostAsync("/api/chickenchaser/settings", new StringContent(JsonSerializer.Serialize(new ChickenChaserSettings { Mode = "help", TaskMode = "VIDEO", Model = "ACE-Step-acestep-5Hz-lm-0.6B-pytorch", Temperature = 9, TopP = -2, IgnoredNotificationCategories = new List<string> { " Updates ", "updates" } }), Encoding.UTF8, "application/json"));
            savedChicken.EnsureSuccessStatusCode();
            string chickenSettings = await savedChicken.Content.ReadAsStringAsync();
            using (JsonDocument chickenJson = JsonDocument.Parse(chickenSettings))
            {
                JsonElement saved = chickenJson.RootElement.GetProperty("settings");
                Assert.AreEqual("help", saved.GetProperty("mode").GetString());
                Assert.AreEqual(2, saved.GetProperty("temperature").GetDouble());
                Assert.AreEqual(0, saved.GetProperty("topP").GetDouble());
                Assert.AreEqual(3, saved.GetProperty("schemaVersion").GetInt32());
                Assert.AreEqual("video", saved.GetProperty("taskMode").GetString());
                Assert.AreEqual("auto", saved.GetProperty("model").GetString());
                Assert.AreEqual("updates", saved.GetProperty("ignoredNotificationCategories")[0].GetString());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task Authenticate(HeirowLlm proxy, NetHttpClient client)
    {
        const string username = "picturebank-test";
        const string password = "correct horse battery staple";
        MethodInfo registration = typeof(HeirowLlm).GetMethod("HandleWebAuthRegistrationRequest", BindingFlags.NonPublic | BindingFlags.Instance)!;
        registration.Invoke(proxy, new object?[] { null, new SocketJack.Net.HttpRequest { Method = "POST", Path = "/api/web-auth/registration-request", Body = JsonSerializer.Serialize(new { username, password }) } });
        proxy.ApproveWebAuthRegistrationRequest(proxy.GetPendingWebAuthRegistrationRequests().Single().Id);
        using HttpResponseMessage login = await client.PostAsync("/api/web-auth/login", new StringContent(JsonSerializer.Serialize(new { username, password, remember = true }), Encoding.UTF8, "application/json"));
        login.EnsureSuccessStatusCode();
    }

    private static int NextPort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
}
