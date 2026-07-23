using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace JackLLM;

internal sealed record WindowsHelloProof(string PublicKey, string Signature, string Attestation);

internal static class WindowsHelloAuthenticator {
#if DEBUG
    private const string CredentialName = "SocketJack.JackLLM.Workstation.Development.v1";
#else
    private const string CredentialName = "SocketJack.JackLLM.Workstation.Official.v1";
#endif

    public static async Task<WindowsHelloProof> CreateAndSignAsync(byte[] challenge) {
        if (!await KeyCredentialManager.IsSupportedAsync())
            throw new InvalidOperationException("Windows Hello is not configured or this device does not support it.");
        KeyCredentialRetrievalResult created = await KeyCredentialManager.RequestCreateAsync(CredentialName, KeyCredentialCreationOption.ReplaceExisting);
        if (created.Status != KeyCredentialStatus.Success)
            throw new InvalidOperationException("Windows Hello key creation failed: " + created.Status);
        return await CreateProofAsync(created.Credential, challenge, requireAttestation: true);
    }

    public static async Task<WindowsHelloProof> OpenAndSignAsync(byte[] challenge) {
        if (!await KeyCredentialManager.IsSupportedAsync())
            throw new InvalidOperationException("Windows Hello is not configured or this device does not support it.");
        KeyCredentialRetrievalResult opened = await KeyCredentialManager.OpenAsync(CredentialName);
        if (opened.Status != KeyCredentialStatus.Success)
            throw new InvalidOperationException("The enrolled Windows Hello key is unavailable: " + opened.Status);
        return await CreateProofAsync(opened.Credential, challenge, requireAttestation: false);
    }

    private static async Task<WindowsHelloProof> CreateProofAsync(KeyCredential credential, byte[] challenge, bool requireAttestation) {
        var challengeBuffer = CryptographicBuffer.CreateFromByteArray(challenge);
        KeyCredentialOperationResult signed = await credential.RequestSignAsync(challengeBuffer);
        if (signed.Status != KeyCredentialStatus.Success)
            throw new InvalidOperationException("Windows Hello did not approve the request: " + signed.Status);
        CryptographicBuffer.CopyToByteArray(credential.RetrievePublicKey(), out byte[] publicKey);
        CryptographicBuffer.CopyToByteArray(signed.Result, out byte[] signature);
        string attestationValue = "";
        if (requireAttestation) {
            KeyCredentialAttestationResult attestation = await credential.GetAttestationAsync();
            if (attestation.Status != KeyCredentialAttestationStatus.Success || attestation.AttestationBuffer == null)
                throw new InvalidOperationException("TPM key attestation is required but Windows returned: " + attestation.Status);
            CryptographicBuffer.CopyToByteArray(attestation.AttestationBuffer, out byte[] attestationBytes);
            if (attestationBytes.Length == 0)
                throw new InvalidOperationException("Windows Hello did not return TPM attestation data.");
            attestationValue = Convert.ToBase64String(attestationBytes);
        }
        return new WindowsHelloProof(Convert.ToBase64String(publicKey), Convert.ToBase64String(signature), attestationValue);
    }
}
