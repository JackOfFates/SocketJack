param(
    [string]$GitProtocol = "https"
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

$gh = Resolve-Gh
if (-not $gh) {
    throw "GitHub CLI was not found. Run tools\Install-GitHubCli.ps1 first."
}

& $gh auth status
if ($LASTEXITCODE -eq 0) {
    Write-Host "GitHub CLI is already authenticated."
    exit 0
}

Write-Host "Starting GitHub CLI browser authentication."
& $gh auth login --web --git-protocol $GitProtocol
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI authentication failed. Exit code: $LASTEXITCODE"
}

& $gh auth status
