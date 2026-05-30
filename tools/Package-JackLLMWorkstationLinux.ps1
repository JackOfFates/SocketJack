param(
    [string]$Configuration = "Release",
    [string]$Version = "1:26.0.1",
    [string]$OutputDir = "",
    [switch]$SkipPublish
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptRoot "..")
$NativeProject = Join-Path $RepoRoot "JackLLM.Workstation\JackLLM.Workstation.csproj"
$WpfProject = Join-Path $RepoRoot "JackLLM\JackLLM.csproj"
$LinuxPackageScript = Join-Path $RepoRoot "tools\linux\package-jackllm-workstation-deb.sh"
$NativePublishDir = Join-Path $RepoRoot "JackLLM.Workstation\bin\$Configuration\net8.0\linux-x64\publish"
$WpfPublishDir = Join-Path $RepoRoot "JackLLM\bin\$Configuration\net8.0-windows7.0\win-x64\publish"
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "artifacts\linux-installer"
}

function Invoke-External {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$Description
    )

    Write-Host "==> $Description"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Test-WslHasDistribution {
    $wsl = Get-Command "wsl.exe" -ErrorAction SilentlyContinue
    if (-not $wsl) {
        return $false
    }

    $output = & $wsl.Source -l -q 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    return ($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1) -ne $null
}

if (-not $SkipPublish) {
    Invoke-External -FilePath "dotnet" -Arguments @(
        "publish",
        $NativeProject,
        "--configuration", $Configuration,
        "--runtime", "linux-x64",
        "--self-contained", "true",
        "--nologo",
        "-v:minimal"
    ) -Description "Publishing JackLLM.Workstation for linux-x64"

    Invoke-External -FilePath "dotnet" -Arguments @(
        "publish",
        $WpfProject,
        "--configuration", $Configuration,
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--nologo",
        "-v:minimal"
    ) -Description "Publishing JackLLM WPF for Wine"
}

if (-not (Test-Path -LiteralPath (Join-Path $NativePublishDir "JackLLM.Workstation"))) {
    throw "Native Linux publish output missing: $NativePublishDir"
}
if (-not (Test-Path -LiteralPath (Join-Path $WpfPublishDir "JackLLM.exe"))) {
    throw "WPF Wine publish output missing: $WpfPublishDir"
}

$dpkgDeb = Get-Command "dpkg-deb" -ErrorAction SilentlyContinue
$bash = Get-Command "bash" -ErrorAction SilentlyContinue
if ($dpkgDeb -and $bash) {
    Invoke-External -FilePath $bash.Source -Arguments @(
        $LinuxPackageScript,
        "--configuration", $Configuration,
        "--version", $Version,
        "--output", $OutputDir,
        "--skip-publish"
    ) -Description "Building Debian package"
    return
}

if (Test-WslHasDistribution) {
    $repoForWsl = (wsl.exe wslpath -a $RepoRoot.Path).Trim()
    $outputForWsl = (wsl.exe wslpath -a ([IO.Path]::GetFullPath($OutputDir))).Trim()
    $scriptForWsl = "$repoForWsl/tools/linux/package-jackllm-workstation-deb.sh"
    Invoke-External -FilePath "wsl.exe" -Arguments @(
        "bash",
        $scriptForWsl,
        "--configuration", $Configuration,
        "--version", $Version,
        "--output", $outputForWsl,
        "--skip-publish"
    ) -Description "Building Debian package through WSL"
    return
}

$message = @"
Published Linux installer inputs, but no Debian packaging environment is available on this Windows machine.

Native publish:
  $NativePublishDir
WPF/Wine publish:
  $WpfPublishDir

To create the .deb, run this on a Debian/Ubuntu machine with dpkg-deb installed:
  bash tools/linux/package-jackllm-workstation-deb.sh --configuration '$Configuration' --version '$Version' --output artifacts/linux-installer --skip-publish
"@
throw $message
