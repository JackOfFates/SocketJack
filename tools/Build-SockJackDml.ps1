param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

function Resolve-MSBuild {
    $candidates = @(
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $cmd = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    throw "MSBuild.exe with C++ build tools was not found. Install Visual Studio C++ workload or Build Tools for Visual Studio."
}

$project = Join-Path (Get-Location) "LlmRuntime\SockJackDml\Native\SockJackDml.Native.vcxproj"
if (-not (Test-Path -LiteralPath $project)) {
    throw "SockJackDml project not found: $project"
}

$msbuild = Resolve-MSBuild
& $msbuild $project /m /nologo /p:Configuration=$Configuration /p:Platform=$Platform
if ($LASTEXITCODE -ne 0) {
    throw "SockJackDml native build failed. Exit code: $LASTEXITCODE"
}

$output = Join-Path (Get-Location) "Tools\DirectML\SockJackDml.dll"
if (-not (Test-Path -LiteralPath $output)) {
    throw "SockJackDml build completed, but the DLL was not found: $output"
}

Write-Host "SockJackDml built: $output"

