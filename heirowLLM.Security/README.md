# heirowLLM Workstation security

heirowLLM Workstation authenticates through the `heirowLLMSecurityBroker` Windows service before it creates the main window or starts localhost services. Release clients must pass Authenticode publisher and signed-manifest validation. Debug clients use a different named pipe, Windows Hello credential name, registry key, and credential store.

Official installer builds must provide these public values:

- `PublisherCertificateSha256`: SHA-256 fingerprint of the Authenticode publisher certificate.
- `ManifestPublicKeyBase64`: Base64-encoded ECDSA P-256 SubjectPublicKeyInfo used to verify `release-manifest.json`.

Keep the corresponding private signing keys outside the repository. After publishing and Authenticode-signing the binaries, run `tools/Sign-heirowLLMReleaseManifest.ps1` against the publish directory. The manifest signature covers SHA-256 hashes; MD5 is not accepted for update or executable trust decisions.

Enrollment generates a `.heirowllm-recovery` file and defaults its save location to the user's Documents folder. The complete file is password protected with an Argon2id-derived key and AES-256-GCM; the recovery key and portable envelope never appear as plaintext in the saved file. By default, the workstation password used during enrollment protects the file. Store it offline or in a protected vault. Recovery and HWID rebinding require the file password, elevation, and Windows Hello, and create a new TPM-bound key on the replacement PC.
