using Microsoft.Maui.Storage;

namespace JackLLM.Mobile.Services;

public sealed class SecureCredentialStore
{
    private const string SocketJackTokenKey = "jackllm.mobile.socketjack.token";

    public Task SetServerTokenAsync(string serverKey, string token) =>
        SecureStorage.Default.SetAsync("jackllm.mobile.server." + Normalize(serverKey), token ?? "");

    public Task<string?> GetServerTokenAsync(string serverKey) =>
        SecureStorage.Default.GetAsync("jackllm.mobile.server." + Normalize(serverKey));

    public void RemoveServerToken(string serverKey) =>
        SecureStorage.Default.Remove("jackllm.mobile.server." + Normalize(serverKey));

    public Task SetSocketJackTokenAsync(string token) => SecureStorage.Default.SetAsync(SocketJackTokenKey, token ?? "");
    public Task<string?> GetSocketJackTokenAsync() => SecureStorage.Default.GetAsync(SocketJackTokenKey);
    public void SignOutSocketJack() => SecureStorage.Default.Remove(SocketJackTokenKey);

    private static string Normalize(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value ?? ""))).ToLowerInvariant();
}
