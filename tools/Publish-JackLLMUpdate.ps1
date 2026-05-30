param(
    [string]$UpdateRoot = "C:\JackLLM\Update",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$ProductVersion = "1.0.5",
    [string]$LinuxWorkstationVersion = "1:26.0.1",
    [string]$LinuxWorkstationDebPath = "",
    [switch]$SkipLinuxWorkstationDeb,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$updateRootPath = [System.IO.Path]::GetFullPath($UpdateRoot)
$publishDir = Join-Path $repoRoot "artifacts\JackLLM\publish"
$bootstrapDir = Join-Path $repoRoot "artifacts\JackLLMBridgeInstaller\publish"
$wixProject = Join-Path $repoRoot "JackLLMInstaller\JackLLMInstaller.wixproj"
$linuxDebBuildScript = Join-Path $repoRoot "tools\Build-LlmWorkstationLinuxDeb.ps1"
$linuxDebFileName = "LlmWorkstation_Linux64.deb"

function Assert-SafeUpdateRoot {
    param([string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($fullPath) -or
        [string]::IsNullOrWhiteSpace($root) -or
        $fullPath.Equals($root.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.Length -lt 8) {
        throw "Refusing to sync unsafe update root '$fullPath'."
    }

    return $fullPath
}

function Get-UpdateRelativePath {
    param(
        [string]$Root,
        [string]$Path
    )

    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.Equals($fullRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$fullPath' escapes update root '$fullRoot'."
    }

    if ($fullPath.Equals($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return ""
    }

    return $fullPath.Substring($prefix.Length).Replace([System.IO.Path]::DirectorySeparatorChar, '/').Replace([System.IO.Path]::AltDirectorySeparatorChar, '/').Trim('/')
}

function Test-JackLlmUpdatePayloadFile {
    param([string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        return $false
    }

    $normalized = $RelativePath.Replace('\', '/').Trim('/')
    if ($normalized.StartsWith('/', [System.StringComparison]::Ordinal) -or
        $normalized.IndexOf('../', [System.StringComparison]::Ordinal) -ge 0 -or
        $normalized.Equals('..', [System.StringComparison]::Ordinal)) {
        return $false
    }

    $blockedSegments = @(
        'agents', 'artifacts', 'cache', 'caches', '.cache', 'config', 'configs',
        'data', 'database', 'databases', 'downloads', 'jackllmchat', 'log', 'logs',
        'models', 'completemodels', 'profile', 'profiles', 'sessionfiles', 'sessions',
        'settings', 'sockjackdml', 'temp', 'tmp', 'tools', 'uploads', 'userdata',
        'user-data', 'workspace', 'workspaces'
    )
    foreach ($segment in $normalized.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)) {
        if ($blockedSegments -contains $segment.ToLowerInvariant()) {
            return $false
        }
    }

    $fileName = [System.IO.Path]::GetFileName($normalized)
    if ([string]::IsNullOrWhiteSpace($fileName)) {
        return $false
    }

    $blockedFileNames = @(
        '.socketjack-update.meta', 'appsettings.json', 'auth.json', 'dynamicupdates.json',
        'jackllm.settings.json', 'lastupdates.json', 'updater-config.json', 'updater-status.json'
    )
    if ($blockedFileNames -contains $fileName.ToLowerInvariant()) {
        return $false
    }

    $extension = [System.IO.Path]::GetExtension($fileName).ToLowerInvariant()
    $blockedExtensions = @(
        '.bak', '.cache', '.config', '.db', '.iobj', '.ipdb', '.lib', '.log', '.map',
        '.old', '.orig', '.pdb', '.sqlite', '.sqlite3', '.suo', '.tmp', '.user',
        '.wixpdb', '.xml'
    )
    if ($blockedExtensions -contains $extension) {
        return $false
    }

    if ($fileName.EndsWith('.json', [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $fileName.EndsWith('.runtimeconfig.json', [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $fileName.EndsWith('.deps.json', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    return $true
}

function Sync-JackLlmUpdatePayload {
    param(
        [string]$SourceRoot,
        [string]$TargetRoot
    )

    $safeTargetRoot = Assert-SafeUpdateRoot $TargetRoot
    if (-not (Test-Path -LiteralPath $SourceRoot)) {
        throw "Publish source folder was not found: $SourceRoot"
    }

    New-Item -ItemType Directory -Force -Path $safeTargetRoot | Out-Null
    $desired = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $copied = 0
    $skipped = 0
    foreach ($sourceFile in Get-ChildItem -LiteralPath $SourceRoot -File -Recurse) {
        $relativePath = Get-UpdateRelativePath -Root $SourceRoot -Path $sourceFile.FullName
        if (-not (Test-JackLlmUpdatePayloadFile $relativePath)) {
            $skipped++
            continue
        }

        [void]$desired.Add($relativePath)
        $destinationPath = Join-Path $safeTargetRoot ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destinationDirectory = [System.IO.Path]::GetDirectoryName($destinationPath)
        if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
            New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        }
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationPath -Force
        [System.IO.File]::SetLastWriteTimeUtc($destinationPath, $sourceFile.LastWriteTimeUtc)
        $copied++
    }

    $deleted = 0
    foreach ($targetFile in Get-ChildItem -LiteralPath $safeTargetRoot -File -Recurse -ErrorAction SilentlyContinue) {
        $relativePath = Get-UpdateRelativePath -Root $safeTargetRoot -Path $targetFile.FullName
        if ($desired.Contains($relativePath) -and (Test-JackLlmUpdatePayloadFile $relativePath)) {
            continue
        }
        Remove-Item -LiteralPath $targetFile.FullName -Force
        $deleted++
    }

    foreach ($directory in Get-ChildItem -LiteralPath $safeTargetRoot -Directory -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName -Descending) {
        if ((Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue | Select-Object -First 1) -ne $null) {
            continue
        }
        Remove-Item -LiteralPath $directory.FullName -Force
    }

    Write-Host "Synced JackLLM runnable payload: $copied files copied, $skipped source files skipped, $deleted stale target files deleted."
}

function Resolve-LinuxWorkstationDeb {
    if ($SkipLinuxWorkstationDeb) {
        return ""
    }

    $debPath = $LinuxWorkstationDebPath
    if ([string]::IsNullOrWhiteSpace($debPath)) {
        $debPath = Join-Path $repoRoot "artifacts\linux-installer\$linuxDebFileName"
        powershell -NoProfile -ExecutionPolicy Bypass -File $linuxDebBuildScript `
            -Configuration $Configuration `
            -Version $LinuxWorkstationVersion `
            -OutputPath $debPath | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $debPath = [System.IO.Path]::GetFullPath($debPath)
    if (-not (Test-Path -LiteralPath $debPath)) {
        throw "Expected Linux workstation Debian package was not found: $debPath"
    }

    return $debPath
}

if ($Clean) {
    $allowedDefault = [System.IO.Path]::GetFullPath("C:\JackLLM\Update")
    if (-not $updateRootPath.Equals($allowedDefault, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean non-default update root '$updateRootPath'. Omit -Clean or use C:\JackLLM\Update."
    }
    if (Test-Path -LiteralPath $updateRootPath) {
        Remove-Item -LiteralPath $updateRootPath -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path $updateRootPath | Out-Null

dotnet build $wixProject `
    -c $Configuration `
    -p:PayloadDir=$publishDir `
    -p:ProductVersion=$ProductVersion
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$requiredPayloadFiles = @(
    "JackLLM.exe",
    "JackLLMUpdater.exe",
    "JackLLMCompanion.exe"
)
foreach ($requiredPayloadFile in $requiredPayloadFiles) {
    $requiredPayloadPath = Join-Path $publishDir $requiredPayloadFile
    if (-not (Test-Path -LiteralPath $requiredPayloadPath)) {
        throw "Expected published payload file was not found: $requiredPayloadPath"
    }
}

Sync-JackLlmUpdatePayload -SourceRoot $publishDir -TargetRoot $updateRootPath

dotnet publish (Join-Path $repoRoot "JackLLMBridgeInstaller\JackLLMBridgeInstaller.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=false `
    -o $bootstrapDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$bootstrapExe = Join-Path $bootstrapDir "JackLLM-Setup.exe"
if (-not (Test-Path -LiteralPath $bootstrapExe)) {
    throw "Expected bootstrap installer executable was not found: $bootstrapExe"
}
Copy-Item -LiteralPath $bootstrapExe -Destination (Join-Path $updateRootPath "JackLLM-Setup.exe") -Force

$msiCandidates = @(
    (Join-Path $repoRoot "JackLLMInstaller\bin\x64\$Configuration\JackLLM-Setup.msi"),
    (Join-Path $repoRoot "JackLLMInstaller\bin\$Configuration\JackLLM-Setup.msi")
)
$msiPath = $msiCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { Get-Item -LiteralPath $_ } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($msiPath)) {
    throw "Expected MSI installer was not found in any candidate path: $($msiCandidates -join ', ')"
}
Copy-Item -LiteralPath $msiPath -Destination (Join-Path $updateRootPath (Split-Path -Leaf $msiPath)) -Force

$linuxDebPath = Resolve-LinuxWorkstationDeb
if (-not [string]::IsNullOrWhiteSpace($linuxDebPath)) {
    Copy-Item -LiteralPath $linuxDebPath -Destination (Join-Path $updateRootPath $linuxDebFileName) -Force
}

Write-Host "Published JackLLM update files to $updateRootPath"
Write-Host "Public URL: https://socketjack.com/update/"
if (-not [string]::IsNullOrWhiteSpace($linuxDebPath)) {
    Write-Host "Linux workstation installer URL: https://SocketJack.com/Update/$linuxDebFileName"
}
Write-Host "Metadata URL: https://socketjack.com/update/meta"
