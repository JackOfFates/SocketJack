param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string]$CertificatePath,

    [string]$CertificatePassword,

    [string]$CertificateThumbprint,

    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    throw "Installer or executable was not found: $Path"
}

$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signTool) {
    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitsRoot) {
        $signTool = Get-ChildItem -Path $kitsRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
    }
}

if (-not $signTool) {
    throw "signtool.exe was not found. Install the Windows SDK."
}

$args = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256")
if ($CertificatePath) {
    if (-not (Test-Path -LiteralPath $CertificatePath)) {
        throw "Certificate file was not found: $CertificatePath"
    }
    $args += @("/f", $CertificatePath)
    if ($CertificatePassword) {
        $args += @("/p", $CertificatePassword)
    }
} elseif ($CertificateThumbprint) {
    $args += @("/sha1", $CertificateThumbprint)
} else {
    throw "Provide CertificatePath or CertificateThumbprint from a secure signing environment."
}

$args += $Path
& $signTool.Source @args
if ($LASTEXITCODE -ne 0) {
    throw "signtool failed with exit code $LASTEXITCODE"
}
