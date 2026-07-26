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
        string first = LmVsProxy.HashSecret("123456");
        string second = LmVsProxy.HashSecret("123456");

        Assert.AreEqual(first, second);
        Assert.AreEqual(64, first.Length);
        Assert.IsFalse(first.Contains("123456", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FixedEquals_RejectsDifferentOrMalformedValues()
    {
        string expected = LmVsProxy.HashSecret("pairing-token");

        Assert.IsTrue(LmVsProxy.FixedEquals(expected, expected));
        Assert.IsFalse(LmVsProxy.FixedEquals(expected, LmVsProxy.HashSecret("other-token")));
        Assert.IsFalse(LmVsProxy.FixedEquals(expected, "short"));
    }

    [TestMethod]
    public void WorkstationAccountsAreTheDefaultAuthenticationAuthority()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-workstation-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435, 0, dataRoot);

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
        MethodInfo? routeCheck = typeof(LmVsProxy).GetMethod(
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
        string dataRoot = Path.Combine(Path.GetTempPath(), "jackllm-registration-approval-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435, 0, dataRoot);
            Assert.IsFalse(proxy.AllowOpenRegistration);

            MethodInfo? directRegister = typeof(LmVsProxy).GetMethod(
                "HandleWebAuthRegisterRequest",
                BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo? requestRegistration = typeof(LmVsProxy).GetMethod(
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
}
