param(
    [string]$ExistingRunnerPath,

    [string]$DownloadUrl,

    [string]$DestinationPath = ".\Tools\DirectML\LlmRuntime.DirectMlRunner.exe",

    [string]$Configuration = "Debug",

    [switch]$SkipNativeSockJackDml,

    [switch]$SetUserEnvironmentVariable
)

$ErrorActionPreference = "Stop"

function Resolve-Destination([string]$PathValue) {
    if ([IO.Path]::IsPathRooted($PathValue)) {
        return $PathValue
    }

    return Join-Path (Get-Location) $PathValue
}

function Install-RunnerFromDirectory([string]$DirectoryPath, [string]$Destination) {
    $preferred = @(
        "LlmRuntime.DirectMlRunner.exe",
        "llm-directml-gguf-runner.exe",
        "llama-directml.exe",
        "llama-cli.exe"
    )

    foreach ($name in $preferred) {
        $candidate = Get-ChildItem -LiteralPath $DirectoryPath -Recurse -Filter $name -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($candidate) {
            Copy-Item -LiteralPath $candidate.FullName -Destination $Destination -Force
            return
        }
    }

    throw "No DirectML GGUF runner executable was found in extracted package."
}

function Copy-DirectoryContentsBestEffort([string]$SourceDirectory, [string]$DestinationDirectory) {
    $sourceRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length)
        $target = Join-Path $DestinationDirectory $relative
        $targetDirectory = Split-Path -Parent $target
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null

        try {
            if (Test-Path -LiteralPath $target) {
                $existing = Get-Item -LiteralPath $target
                if ($existing.Length -eq $_.Length -and $existing.LastWriteTimeUtc -ge $_.LastWriteTimeUtc) {
                    return
                }
            }

            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
        catch {
            if (-not (Test-Path -LiteralPath $target)) {
                throw
            }

            Write-Warning "Keeping existing locked runner dependency: $target"
        }
    }
}

$destination = Resolve-Destination $DestinationPath
$destinationDirectory = Split-Path -Parent $destination
New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($ExistingRunnerPath) -and [string]::IsNullOrWhiteSpace($DownloadUrl)) {
    $projectPath = Join-Path (Get-Location) "LlmRuntime\LlmRuntime.csproj"
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "No runner input was supplied and the source project was not found: $projectPath"
    }

    dotnet build $projectPath --configuration $Configuration --nologo `
        -p:BuildDirectMlRunnerExe=true
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build the DirectML runner from LlmRuntime. Exit code: $LASTEXITCODE"
    }

    $builtRunner = Join-Path (Get-Location) "LlmRuntime\bin\$Configuration\net8.0\LlmRuntime.DirectMlRunner.exe"
    if (-not (Test-Path -LiteralPath $builtRunner)) {
        throw "Built runner was not found: $builtRunner"
    }

    $ExistingRunnerPath = $builtRunner
}

if (-not $SkipNativeSockJackDml) {
    $nativeBuildScript = Join-Path (Get-Location) "tools\Build-SockJackDml.ps1"
    if (Test-Path -LiteralPath $nativeBuildScript) {
        & $nativeBuildScript -Configuration $Configuration
    }
}

if (-not [string]::IsNullOrWhiteSpace($ExistingRunnerPath)) {
    if (-not (Test-Path -LiteralPath $ExistingRunnerPath)) {
        throw "Existing runner path was not found: $ExistingRunnerPath"
    }

    $sourceDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $ExistingRunnerPath)
    Copy-DirectoryContentsBestEffort -SourceDirectory $sourceDirectory -DestinationDirectory $destinationDirectory
    if ((Split-Path -Leaf $ExistingRunnerPath) -ne (Split-Path -Leaf $destination)) {
        Copy-Item -LiteralPath $ExistingRunnerPath -Destination $destination -Force
    }
}
else {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("llmruntime-directml-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    try {
        $downloadPath = Join-Path $tempRoot ([IO.Path]::GetFileName(([Uri]$DownloadUrl).AbsolutePath))
        if ([string]::IsNullOrWhiteSpace([IO.Path]::GetFileName($downloadPath))) {
            $downloadPath = Join-Path $tempRoot "directml-runner-download"
        }

        Invoke-WebRequest -Uri $DownloadUrl -OutFile $downloadPath
        if ($downloadPath.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) {
            $extractPath = Join-Path $tempRoot "extract"
            Expand-Archive -LiteralPath $downloadPath -DestinationPath $extractPath -Force
            Install-RunnerFromDirectory -DirectoryPath $extractPath -Destination $destination
        }
        else {
            Copy-Item -LiteralPath $downloadPath -Destination $destination -Force
        }
    }
    finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $destination)) {
    throw "DirectML runner was not installed: $destination"
}

if ($SetUserEnvironmentVariable) {
    [Environment]::SetEnvironmentVariable("LLMRUNTIME_DIRECTML_GGUF_RUNNER", $destination, "User")
}

Write-Host "DirectML GGUF runner installed: $destination"
Write-Host "Configure LlmRuntimeOptions.DirectMlGgufRunnerPath or set LLMRUNTIME_DIRECTML_GGUF_RUNNER to this path."

