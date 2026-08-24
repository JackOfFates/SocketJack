using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using System.Reflection;
using System.Text.Json;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class MobilePairingSecurityTests
{
    [TestMethod]
    public void HashSecret_IsStableAndDoesNotStorePlaintext()
    {
        string first = HeirowLlm.HashSecret("123456");
        string second = HeirowLlm.HashSecret("123456");

        Assert.AreEqual(first, second);
        Assert.AreEqual(64, first.Length);
        Assert.IsFalse(first.Contains("123456", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FixedEquals_RejectsDifferentOrMalformedValues()
    {
        string expected = HeirowLlm.HashSecret("pairing-token");

        Assert.IsTrue(HeirowLlm.FixedEquals(expected, expected));
        Assert.IsFalse(HeirowLlm.FixedEquals(expected, HeirowLlm.HashSecret("other-token")));
        Assert.IsFalse(HeirowLlm.FixedEquals(expected, "short"));
    }

    [TestMethod]
    public void WorkstationAccountsAreTheDefaultAuthenticationAuthority()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "heirowllm-workstation-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435, 0, dataRoot);

            Assert.IsTrue(proxy.StoreLocalWebAuthAccounts);
            Assert.IsTrue(proxy.RequireWorkstationUserAuthentication);
            Assert.IsFalse(proxy.UseSocketJackMasterAuth);
            Assert.IsFalse(proxy.AllowOpenRegistration);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void WorkstationPreLoginRoutesExposeOnlyAccountBootstrapApis()
    {
        MethodInfo? routeCheck = typeof(HeirowLlm).GetMethod(
            "IsWorkstationUnauthenticatedRoute",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(routeCheck);
        bool Allowed(string method, string path) =>
            (bool)(routeCheck.Invoke(null, new object[] { method, path }) ?? false);

        Assert.IsTrue(Allowed("GET", "/"));
        Assert.IsTrue(Allowed("GET", "/api/web-auth/session"));
        Assert.IsTrue(Allowed("POST", "/api/web-auth/login"));
        Assert.IsTrue(Allowed("POST", "/api/web-auth/register"));
        Assert.IsTrue(Allowed("POST", "/api/web-auth/registration-request"));
        Assert.IsTrue(Allowed("POST", "/api/mobile/pairing/complete"));

        Assert.IsFalse(Allowed("GET", "/api/health"));
        Assert.IsFalse(Allowed("GET", "/api/mobile/status"));
        Assert.IsFalse(Allowed("GET", "/api/web-chat/ws"));
        Assert.IsFalse(Allowed("GET", "/api/models"));
        Assert.IsFalse(Allowed("POST", "/api/chat"));
    }

    [TestMethod]
    public void ClosedRegistrationCreatesAnAdminApprovedWorkstationAccount()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "heirowllm-registration-approval-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435, 0, dataRoot);
            Assert.IsFalse(proxy.AllowOpenRegistration);

            MethodInfo? directRegister = typeof(HeirowLlm).GetMethod(
                "HandleWebAuthRegisterRequest",
                BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo? requestRegistration = typeof(HeirowLlm).GetMethod(
                "HandleWebAuthRegistrationRequest",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(directRegister);
            Assert.IsNotNull(requestRegistration);

            var directRequest = new HttpRequest
            {
                Method = "POST",
                Path = "/api/web-auth/register",
                Body = """{"username":"first-user","password":"correct horse battery staple"}"""
            };
            string directResponse = (string)(directRegister.Invoke(proxy, new object?[] { null, directRequest }) ?? "");
            StringAssert.Contains(directResponse, "Open registration is disabled");
            Assert.AreEqual(0, proxy.GetWebAuthUserDiagnostics().Count);

            var approvalRequest = new HttpRequest
            {
                Method = "POST",
                Path = "/api/web-auth/registration-request",
                Body = """{"username":"first-user","password":"correct horse battery staple"}"""
            };
            string pendingResponse = (string)(requestRegistration.Invoke(proxy, new object?[] { null, approvalRequest }) ?? "");
            using JsonDocument pendingJson = JsonDocument.Parse(pendingResponse);
            Assert.IsTrue(pendingJson.RootElement.GetProperty("pending").GetBoolean());

            var pending = proxy.GetPendingWebAuthRegistrationRequests();
            Assert.AreEqual(1, pending.Count);
            proxy.ApproveWebAuthRegistrationRequest(pending[0].Id);

            var users = proxy.GetWebAuthUserDiagnostics();
            Assert.AreEqual(1, users.Count);
            Assert.AreEqual("first-user", users[0].UserName);
            Assert.IsTrue(users[0].IsAdministrator, "The first administrator-approved account must be able to approve later requests.");
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public void WorkstationAccountSupportsConcurrentRememberedSessionsAndPerDeviceLogout()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "heirowllm-concurrent-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string userName = "multi-device-user";
            const string password = "correct horse battery staple";
            string secondToken;
            using (var proxy = new HeirowLlm("127.0.0.1", 11434, 11435, 0, dataRoot))
            {

                InvokeWebAuth(proxy, "HandleWebAuthRegistrationRequest", new HttpRequest
                {
                    Method = "POST",
                    Path = "/api/web-auth/registration-request",
                    Body = $$"""{"username":"{{userName}}","password":"{{password}}"}"""
                });
                var pending = proxy.GetPendingWebAuthRegistrationRequests();
                Assert.AreEqual(1, pending.Count);
                proxy.ApproveWebAuthRegistrationRequest(pending[0].Id);

                JsonElement firstLogin = ParseRoot(InvokeWebAuth(proxy, "HandleWebAuthLoginRequest", LoginRequest(userName, password)));
                string firstToken = firstLogin.GetProperty("accessToken").GetString() ?? "";
                DateTimeOffset firstExpiry = DateTimeOffset.Parse(firstLogin.GetProperty("expiresUtc").GetString() ?? "");
                JsonElement secondLogin = ParseRoot(InvokeWebAuth(proxy, "HandleWebAuthLoginRequest", LoginRequest(userName, password)));
                secondToken = secondLogin.GetProperty("accessToken").GetString() ?? "";

                Assert.AreNotEqual(firstToken, secondToken);
                Assert.IsTrue(firstExpiry > DateTimeOffset.UtcNow.AddDays(29), "Remembered sessions should last about 30 days.");
                Assert.IsTrue(IsAuthenticated(proxy, firstToken), "The first device must remain authorized after a second device signs in.");
                Assert.IsTrue(IsAuthenticated(proxy, secondToken), "The second device must be authorized concurrently.");

                InvokeWebAuth(proxy, "HandleWebAuthLogoutRequest", AuthorizedRequest("POST", "/api/web-auth/logout", firstToken));

                Assert.IsFalse(IsAuthenticated(proxy, firstToken), "Logging out one device must revoke only that device token.");
                Assert.IsTrue(IsAuthenticated(proxy, secondToken), "Logging out the first device must not revoke another active device.");
            }

            using var reloadedProxy = new HeirowLlm("127.0.0.1", 11434, 11435, 0, dataRoot);
            Assert.IsTrue(IsAuthenticated(reloadedProxy, secondToken), "Active device sessions must survive a Workstation restart.");
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static HttpRequest LoginRequest(string userName, string password) => new()
    {
        Method = "POST",
        Path = "/api/web-auth/login",
        Body = JsonSerializer.Serialize(new { username = userName, password, remember = true })
    };

    private static HttpRequest AuthorizedRequest(string method, string path, string token)
    {
        var request = new HttpRequest { Method = method, Path = path };
        request.Headers["Authorization"] = "Bearer " + token;
        return request;
    }

    private static bool IsAuthenticated(HeirowLlm proxy, string token)
    {
        string response = InvokeWebAuth(
            proxy,
            "HandleWebAuthSessionRequest",
            AuthorizedRequest("GET", "/api/web-auth/session", token));
        return ParseRoot(response).GetProperty("authenticated").GetBoolean();
    }

    private static string InvokeWebAuth(HeirowLlm proxy, string methodName, HttpRequest request)
    {
        MethodInfo method = typeof(HeirowLlm).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.IsNotNull(method);
        return (string)(method.Invoke(proxy, new object?[] { null, request }) ?? "");
    }

    private static JsonElement ParseRoot(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
