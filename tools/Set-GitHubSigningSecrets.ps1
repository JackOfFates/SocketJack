param(
    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,

    [string]$CertificatePassword,

    [string]$Repository
)

$ErrorActionPreference = "Stop"

function Resolve-Gh {
    $cmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "$env:ProgramFiles\GitHub CLI\gh.exe",
        "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

function ConvertTo-PlainText([securestring]$SecureValue) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

function Set-SecretFromString([string]$Name, [string]$Value, [string[]]$RepoArgs) {
    $temp = [IO.Path]::GetTempFileName()
    try {
        Set-Content -LiteralPath $temp -Value $Value -NoNewline -Encoding ascii
        Get-Content -LiteralPath $temp -Raw | & $script:Gh secret set $Name @RepoArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set GitHub secret $Name."
        }
    }
    finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $CertificatePath)) {
    throw "Certificate not found: $CertificatePath"
}

$script:Gh = Resolve-Gh
if (-not $script:Gh) {
    throw "GitHub CLI was not found. Run tools\Install-GitHubCli.ps1 first."
}

& $script:Gh auth status
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI is not authenticated. Run tools\Initialize-GitHubCliAuth.ps1 first."
}

if ([string]::IsNullOrWhiteSpace($CertificatePassword)) {
    $secure = Read-Host "PFX password" -AsSecureString
    $CertificatePassword = ConvertTo-PlainText $secure
}

$repoArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Repository)) {
    $repoArgs = @("--repo", $Repository)
}

$bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $CertificatePath))
$base64 = [Convert]::ToBase64String($bytes)

Set-SecretFromString -Name "CODE_SIGNING_PFX_BASE64" -Value $base64 -RepoArgs $repoArgs
Set-SecretFromString -Name "CODE_SIGNING_PFX_PASSWORD" -Value $CertificatePassword -RepoArgs $repoArgs

Write-Host "GitHub signing secrets have been configured."
