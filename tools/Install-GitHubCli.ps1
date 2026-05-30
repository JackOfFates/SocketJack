param(
    [switch]$Force
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

$existing = Resolve-Gh
if ($existing -and -not $Force) {
    Write-Host "GitHub CLI already installed: $existing"
    & $existing --version
    exit 0
}

$winget = Get-Command winget -ErrorAction SilentlyContinue
if (-not $winget) {
    throw "winget was not found. Install GitHub CLI manually from https://cli.github.com/ or install winget first."
}

& $winget.Source install --id GitHub.cli --source winget --exact --accept-package-agreements --accept-source-agreements
if ($LASTEXITCODE -ne 0) {
    throw "winget failed to install GitHub CLI. Exit code: $LASTEXITCODE"
}

$gh = Resolve-Gh
if (-not $gh) {
    throw "GitHub CLI installation completed, but gh.exe could not be found."
}

$installDir = Split-Path -Parent $gh
$pathParts = [Environment]::GetEnvironmentVariable("Path", "User") -split ';' | Where-Object { $_ }
if ($pathParts -notcontains $installDir) {
    [Environment]::SetEnvironmentVariable("Path", (($pathParts + $installDir) -join ';'), "User")
}

if (($env:Path -split ';') -notcontains $installDir) {
    $env:Path = $env:Path + ";" + $installDir
}

Write-Host "GitHub CLI installed: $gh"
& $gh --version
