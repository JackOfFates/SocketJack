using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace heirowLLM.Security.Tests;

[TestClass]
public sealed class NetworkBindingProviderTests {
    [TestMethod]
    public async Task ConflictingPublicIpProvidersFailClosed() {
        var provider = CreateProvider(new Dictionary<string, string?> {
            ["api.ipify.org"] = "203.0.113.10",
            ["checkip.amazonaws.com"] = "203.0.113.11"
        });
        NetworkBindingResult result = await provider.GetCurrentAsync(CancellationToken.None);
        Assert.IsFalse(result.PublicIpVerified);
        StringAssert.Contains(result.Error, "conflicting");
    }

    [TestMethod]
    public async Task OneValidProviderIsAcceptedWhenTheOtherIsMalformed() {
        var provider = CreateProvider(new Dictionary<string, string?> {
            ["api.ipify.org"] = "not-an-address",
            ["checkip.amazonaws.com"] = "203.0.113.10"
        });
        NetworkBindingResult result = await provider.GetCurrentAsync(CancellationToken.None);
        Assert.IsTrue(result.PublicIpVerified);
        Assert.AreEqual("203.0.113.10", result.PublicIp);
    }

    [TestMethod]
    public async Task UnavailablePublicIpProvidersRequireCachedValidation() {
        var provider = CreateProvider(new Dictionary<string, string?> {
            ["api.ipify.org"] = null,
            ["checkip.amazonaws.com"] = null
        });
        NetworkBindingResult result = await provider.GetCurrentAsync(CancellationToken.None);
        Assert.IsFalse(result.PublicIpVerified);
        Assert.IsNull(result.PublicIp);
    }

    private static NetworkBindingProvider CreateProvider(IReadOnlyDictionary<string, string?> responses) =>
        new(new HttpClient(new StubHandler(responses)));

    private sealed class StubHandler(IReadOnlyDictionary<string, string?> responses) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            string host = request.RequestUri!.Host;
            if (!responses.TryGetValue(host, out string? value) || value == null)
                throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(value)
            });
        }
    }
}
