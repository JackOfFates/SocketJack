param(
    [switch]$Force,
    [switch]$NoVerify
)

$ErrorActionPreference = "Stop"

function Test-Git {
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if (-not $git) {
        return $false
    }

    & $git.Source --version
    return $LASTEXITCODE -eq 0
}

if ((Test-Git) -and -not $Force) {
    Write-Host "Git for Windows is already installed and available on PATH."
    exit 0
}

$winget = Get-Command winget.exe -ErrorAction SilentlyContinue
if (-not $winget) {
    throw "winget.exe was not found. Install Git manually from https://git-scm.com/download/win or install App Installer from Microsoft Store."
}

& $winget.Source install --id Git.Git --source winget --exact --accept-package-agreements --accept-source-agreements
if ($LASTEXITCODE -ne 0) {
    throw "winget failed to install Git for Windows. Exit code: $LASTEXITCODE"
}

if (-not $NoVerify -and -not (Test-Git)) {
    throw "Git installed, but git.exe is not available on PATH in this process. Reopen the terminal or restart the host app."
}

Write-Host "Git for Windows installation check completed."
