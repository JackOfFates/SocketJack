param(
    [string]$Configuration = "Release",
    [string]$Version = "1:26.0.1",
    [string]$OutputPath = "",
    [switch]$SkipPublish,
    [switch]$SkipRemote,
    [string]$RemoteHost = "216.235.101.12",
    [int]$RemotePort = 25,
    [string]$RemoteUser = "wintergrasped",
    [string]$RemoteHostKey = "SHA256:qVC8NKNzNX7Zbm9Ce/3zLIYO/Kh8Pa8UiXGk1My4jZU",
    [string]$RemoteStagingRoot = "/stor2/JackLLMDebBuild",
    [string]$RemotePasswordEnvVar = "SABLE_SSH_PASSWORD"
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $RepoRoot "artifacts\linux-installer\LlmWorkstation_Linux64.deb"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$OutputDir = Split-Path -Parent $OutputPath
$OutputFileName = Split-Path -Leaf $OutputPath
if ([string]::IsNullOrWhiteSpace($OutputFileName)) {
    $OutputFileName = "LlmWorkstation_Linux64.deb"
    $OutputPath = Join-Path $OutputDir $OutputFileName
}
$NativeProject = Join-Path $RepoRoot "JackLLM.Workstation\JackLLM.Workstation.csproj"
$WpfProject = Join-Path $RepoRoot "JackLLM\JackLLM.csproj"
$LinuxPackageScript = Join-Path $RepoRoot "tools\linux\package-jackllm-workstation-deb.sh"
$LinuxCudaScript = Join-Path $RepoRoot "tools\linux\install-jackllm-cuda-pytorch.sh"
$NativePublishDir = Join-Path $RepoRoot "JackLLM.Workstation\bin\$Configuration\net8.0\linux-x64\publish"
$WpfPublishDir = Join-Path $RepoRoot "JackLLM\bin\$Configuration\net8.0-windows7.0\win-x64\publish"
$ArtifactsRoot = Join-Path $RepoRoot "artifacts\linux-installer"
$LocalStagingRoot = Join-Path $ArtifactsRoot "deb-input"
$ArchivePath = Join-Path $ArtifactsRoot "deb-input.tar.gz"

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

function Assert-UnderDirectory {
    param(
        [string]$Path,
        [string]$Parent
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $prefix = $fullParent + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.Equals($fullParent, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$fullPath' is not under '$fullParent'."
    }
}

function Reset-Directory {
    param(
        [string]$Path,
        [string]$SafeParent
    )

    Assert-UnderDirectory -Path $Path -Parent $SafeParent
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Sync-Directory {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Source directory was not found: $Source"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    & robocopy $Source $Destination /MIR /NFL /NDL /NJH /NJS /NC /NS /NP
    $code = $LASTEXITCODE
    if ($code -gt 7) {
        throw "robocopy failed syncing '$Source' to '$Destination' with exit code $code."
    }
}

function Get-PuttyTool {
    param([string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles} "PuTTY\$Name"),
        (Join-Path ${env:ProgramFiles(x86)} "PuTTY\$Name")
    )
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw "$Name was not found. Install PuTTY or add $Name to PATH."
}

function Quote-Bash {
    param([string]$Value)
    if ($null -eq $Value) {
        $Value = ""
    }
    return "'" + ($Value.Replace("'", "'\''")) + "'"
}

function Get-RemotePassword {
    param([string]$EnvVarName)

    $value = [Environment]::GetEnvironmentVariable($EnvVarName)
    if (-not [string]::IsNullOrEmpty($value)) {
        return $value
    }

    $secure = Read-Host -Prompt "SSH password for $RemoteUser@$RemoteHost" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    } finally {
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

function Invoke-LocalDebPackager {
    $dpkgDeb = Get-Command "dpkg-deb" -ErrorAction SilentlyContinue
    $bash = Get-Command "bash" -ErrorAction SilentlyContinue
    if (-not ($dpkgDeb -and $bash)) {
        return $false
    }

    Invoke-External -FilePath $bash.Source -Arguments @(
        $LinuxPackageScript,
        "--configuration", $Configuration,
        "--version", $Version,
        "--output", $OutputDir,
        "--package-file-name", $OutputFileName,
        "--skip-publish"
    ) -Description "Building $OutputFileName with local dpkg-deb"
    return $true
}

function Invoke-WslDebPackager {
    if (-not (Test-WslHasDistribution)) {
        return $false
    }

    $repoForWsl = (wsl.exe wslpath -a $RepoRoot).Trim()
    $outputForWsl = (wsl.exe wslpath -a $OutputDir).Trim()
    $scriptForWsl = "$repoForWsl/tools/linux/package-jackllm-workstation-deb.sh"
    Invoke-External -FilePath "wsl.exe" -Arguments @(
        "bash",
        $scriptForWsl,
        "--configuration", $Configuration,
        "--version", $Version,
        "--output", $outputForWsl,
        "--package-file-name", $OutputFileName,
        "--skip-publish"
    ) -Description "Building $OutputFileName through WSL"
    return $true
}

function Invoke-WindowsDebPackager {
    $windowsPackager = Join-Path $RepoRoot "tools\Package-JackLLMWorkstationLinuxWindows.ps1"
    if (-not (Test-Path -LiteralPath $windowsPackager)) {
        return $false
    }

    Invoke-External -FilePath "powershell" -Arguments @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $windowsPackager,
        "-Configuration",
        $Configuration,
        "-Version",
        $Version,
        "-OutputPath",
        $OutputPath,
        "-NativePublishDir",
        $NativePublishDir,
        "-WpfPublishDir",
        $WpfPublishDir
    ) -Description "Building $OutputFileName with Windows Debian packer"
    return $true
}

function Invoke-RemoteDebPackager {
    if ($SkipRemote) {
        return $false
    }

    $plink = Get-PuttyTool "plink.exe"
    $pscp = Get-PuttyTool "pscp.exe"
    $password = Get-RemotePassword -EnvVarName $RemotePasswordEnvVar
    $remoteTarget = "$RemoteUser@$RemoteHost"
    $runId = "llmworkstation-deb-" + (Get-Date -Format "yyyyMMdd-HHmmss")
    $remoteStage = "$RemoteStagingRoot/$runId"
    $remoteArchive = "$remoteStage/input.tar.gz"
    $remoteDeb = "$remoteStage/out/$OutputFileName"
    $common = @("-batch", "-P", "$RemotePort", "-hostkey", $RemoteHostKey, "-pw", $password)

    Reset-Directory -Path $LocalStagingRoot -SafeParent $ArtifactsRoot
    Reset-Directory -Path (Join-Path $LocalStagingRoot "native") -SafeParent $LocalStagingRoot
    Reset-Directory -Path (Join-Path $LocalStagingRoot "wpf") -SafeParent $LocalStagingRoot
    New-Item -ItemType Directory -Force -Path (Join-Path $LocalStagingRoot "tools\linux") | Out-Null

    Write-Host "==> Staging publish outputs for remote Debian packaging"
    Sync-Directory -Source $NativePublishDir -Destination (Join-Path $LocalStagingRoot "native")
    Sync-Directory -Source $WpfPublishDir -Destination (Join-Path $LocalStagingRoot "wpf")
    Copy-Item -LiteralPath $LinuxPackageScript -Destination (Join-Path $LocalStagingRoot "tools\linux\package-jackllm-workstation-deb.sh") -Force
    Copy-Item -LiteralPath $LinuxCudaScript -Destination (Join-Path $LocalStagingRoot "tools\linux\install-jackllm-cuda-pytorch.sh") -Force

    if (Test-Path -LiteralPath $ArchivePath) {
        Assert-UnderDirectory -Path $ArchivePath -Parent $ArtifactsRoot
        Remove-Item -LiteralPath $ArchivePath -Force
    }
    Invoke-External -FilePath "tar.exe" -Arguments @(
        "-czf", $ArchivePath,
        "-C", $LocalStagingRoot,
        "native", "wpf", "tools"
    ) -Description "Creating remote packaging archive"

    $mkdirCommand = "mkdir -p " + (Quote-Bash $remoteStage)
    Invoke-External -FilePath $plink -Arguments ($common + @($remoteTarget, $mkdirCommand)) -Description "Creating remote packaging folder on $RemoteHost"
    Invoke-External -FilePath $pscp -Arguments ($common + @($ArchivePath, "${remoteTarget}:$remoteArchive")) -Description "Uploading Debian package inputs"

    $remoteCommand = @"
set -Eeuo pipefail
stage=$(Quote-Bash $remoteStage)
root=$(Quote-Bash $RemoteStagingRoot)
case "`$stage" in
  "`$root"/*) ;;
  *) echo "Unsafe remote staging path: `$stage" >&2; exit 2 ;;
esac
rm -rf "`$stage/extracted"
mkdir -p "`$stage/extracted"
tar -xzf $(Quote-Bash $remoteArchive) -C "`$stage/extracted"
chmod +x "`$stage/extracted/tools/linux/package-jackllm-workstation-deb.sh" "`$stage/extracted/tools/linux/install-jackllm-cuda-pytorch.sh"
bash "`$stage/extracted/tools/linux/package-jackllm-workstation-deb.sh" \
  --version $(Quote-Bash $Version) \
  --output "`$stage/out" \
  --native-publish "`$stage/extracted/native" \
  --wpf-publish "`$stage/extracted/wpf" \
  --package-file-name $(Quote-Bash $OutputFileName) \
  --skip-publish
test -f $(Quote-Bash $remoteDeb)
"@
    Invoke-External -FilePath $plink -Arguments ($common + @($remoteTarget, "bash -lc " + (Quote-Bash $remoteCommand))) -Description "Building $OutputFileName on $RemoteHost"

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    if (Test-Path -LiteralPath $OutputPath) {
        Remove-Item -LiteralPath $OutputPath -Force
    }
    Invoke-External -FilePath $pscp -Arguments ($common + @("${remoteTarget}:$remoteDeb", $OutputPath)) -Description "Downloading $OutputFileName"
    return $true
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $ArtifactsRoot | Out-Null

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

$built = Invoke-LocalDebPackager
if (-not $built) {
    $built = Invoke-WslDebPackager
}
if (-not $built) {
    $built = Invoke-WindowsDebPackager
}
if (-not $built) {
    $built = Invoke-RemoteDebPackager
}
if (-not $built) {
    throw "No Debian packaging environment was available. Install dpkg-deb locally, enable WSL, use the Windows Debian packer, or provide $RemotePasswordEnvVar for remote packaging."
}

if (-not (Test-Path -LiteralPath $OutputPath)) {
    throw "Expected Debian package was not produced: $OutputPath"
}

$info = Get-Item -LiteralPath $OutputPath
Write-Host "Built $($info.FullName) ($($info.Length) bytes)"
