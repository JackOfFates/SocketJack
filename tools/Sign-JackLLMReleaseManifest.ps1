param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$PrivateKeyPemPath
)

$ErrorActionPreference = 'Stop'
$publishRoot = [System.IO.Path]::GetFullPath($PublishDirectory)
$privateKeyPath = [System.IO.Path]::GetFullPath($PrivateKeyPemPath)
if (-not [System.IO.Directory]::Exists($publishRoot)) { throw "Publish directory not found: $publishRoot" }
if (-not [System.IO.File]::Exists($privateKeyPath)) { throw "Manifest private key not found: $privateKeyPath" }

$excluded = @('release-manifest.json', 'release-manifest.sig')
$files = [System.IO.Directory]::EnumerateFiles($publishRoot, '*', [System.IO.SearchOption]::AllDirectories) |
    Where-Object { $excluded -notcontains [System.IO.Path]::GetFileName($_) } |
    Sort-Object |
    ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($publishRoot, $_).Replace('\', '/')
        $hash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($_))).ToLowerInvariant()
        [ordered]@{ path = $relative; sha256 = $hash; length = ([System.IO.FileInfo]::new($_)).Length }
    }

$manifest = [ordered]@{
    version = 1
    generatedUtc = [System.DateTimeOffset]::UtcNow.ToString('O')
    files = @($files)
}
$manifestPath = [System.IO.Path]::Combine($publishRoot, 'release-manifest.json')
$signaturePath = [System.IO.Path]::Combine($publishRoot, 'release-manifest.sig')
$json = $manifest | ConvertTo-Json -Depth 5
$bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
[System.IO.File]::WriteAllBytes($manifestPath, $bytes)

$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
try {
    $ecdsa.ImportFromPem([System.IO.File]::ReadAllText($privateKeyPath))
    $signature = $ecdsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    [System.IO.File]::WriteAllText($signaturePath, [System.Convert]::ToBase64String($signature), [System.Text.UTF8Encoding]::new($false))
    Write-Output "ManifestPublicKeyBase64: $([System.Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo()))"
} finally {
    $ecdsa.Dispose()
}

Write-Output "Manifest: $manifestPath"
Write-Output "Signature: $signaturePath"
