# JackLLM Workstation security

JackLLM Workstation authenticates through the `JackLLMSecurityBroker` Windows service before it creates the main window or starts localhost services. Release clients must pass Authenticode publisher and signed-manifest validation. Debug clients use a different named pipe, Windows Hello credential name, registry key, and credential store.

Official installer builds must provide these public values:

- `PublisherCertificateSha256`: SHA-256 fingerprint of the Authenticode publisher certificate.
- `ManifestPublicKeyBase64`: Base64-encoded ECDSA P-256 SubjectPublicKeyInfo used to verify `release-manifest.json`.

Keep the corresponding private signing keys outside the repository. After publishing and Authenticode-signing the binaries, run `tools/Sign-JackLLMReleaseManifest.ps1` against the publish directory. The manifest signature covers SHA-256 hashes; MD5 is not accepted for update or executable trust decisions.

Enrollment produces both a printable recovery value and a `.jackllm-recovery` file. The file contains the recovery key and an Argon2id/AES-GCM protected portable envelope, so possession of the file is sufficient for recovery. Store it offline or in a protected vault. Recovery and HWID rebinding require elevation and create a new Windows Hello key on the replacement PC.
