param(
    [string]$InstallDirectory = "",
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"

function Resolve-InstallDirectory {
    param([string]$RequestedDirectory)

    if (-not [string]::IsNullOrWhiteSpace($RequestedDirectory)) {
        if (-not (Test-Path -LiteralPath $RequestedDirectory -PathType Container)) {
            throw "Install directory was not found: $RequestedDirectory"
        }
        return (Resolve-Path -LiteralPath $RequestedDirectory).Path
    }

    $process = Get-Process -Name "SocketJack.Update" -ErrorAction SilentlyContinue |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.Path) } |
        Select-Object -First 1

    if ($process -and (Test-Path -LiteralPath $process.Path -PathType Leaf)) {
        return Split-Path -Parent $process.Path
    }

    $candidates = @(
        "C:\SocketJack.Update",
        "C:\SocketJackUpdate",
        "C:\Program Files\SocketJack.Update",
        "C:\Program Files\SocketJack Update",
        "$env:USERPROFILE\Desktop\SocketJack.Update",
        "$env:USERPROFILE\Desktop\SocketJack Update"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $candidate "SocketJack.Update.exe") -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Could not find SocketJack.Update.exe. Re-run with -InstallDirectory <folder>."
}

function ConvertTo-JsonDepth {
    param([object]$Value)
    return $Value | ConvertTo-Json -Depth 20
}

$install = Resolve-InstallDirectory -RequestedDirectory $InstallDirectory
$exePath = Join-Path $install "SocketJack.Update.exe"
$settingsPath = Join-Path $install "appsettings.json"
$settingsChanged = $false

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "SocketJack.Update.exe was not found in: $install"
}

if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
} else {
    $settings = [pscustomobject]@{
        BindHost = "127.0.0.1"
        Port = 8500
        DataDirectory = "C:\SocketJackUpdate"
        Channels = @()
    }
}

foreach ($property in @(
    @{ Name = "BindHost"; Value = "127.0.0.1" },
    @{ Name = "Port"; Value = 8500 }
)) {
    if ($settings.PSObject.Properties.Name.Contains($property.Name)) {
        if ($settings.($property.Name) -ne $property.Value) {
            $settings.($property.Name) = $property.Value
            $settingsChanged = $true
        }
    } else {
        Add-Member -InputObject $settings -MemberType NoteProperty -Name $property.Name -Value $property.Value
        $settingsChanged = $true
    }
}

foreach ($staleProperty in @("PublicUrl", "UseSsl", "EnableHttpsRedirect", "SslCertificatePath", "SslCertificatePassword", "SslTargetHost")) {
    if ($settings.PSObject.Properties.Name.Contains($staleProperty)) {
        $settings.PSObject.Properties.Remove($staleProperty)
        $settingsChanged = $true
    }
}

if (-not $settings.PSObject.Properties.Name.Contains("Channels") -or $null -eq $settings.Channels) {
    Add-Member -InputObject $settings -MemberType NoteProperty -Name Channels -Value @()
    $settingsChanged = $true
}

$channels = @($settings.Channels)
$onlineUsers = $channels | Where-Object { $_.Id -ieq "onlineusers-server" } | Select-Object -First 1
$companion = $channels | Where-Object { $_.Id -ieq "jackllm-companion" } | Select-Object -First 1

if ($null -eq $onlineUsers) {
    $channels += [pscustomobject]@{
        Id = "onlineusers-server"
        DisplayName = "OnlineUsers Server"
        UpdateDirectory = "C:\Users\jackoffates\Desktop\wShare Server"
        ManagedProcessName = "OnlineUsers"
        ManagedExecutablePath = "OnlineUsers.exe"
        AutoStartAfterUpdate = $true
    }
    $settings.Channels = $channels
    $settingsChanged = $true
    Write-Host "Added onlineusers-server to $settingsPath"
} else {
    Write-Host "onlineusers-server already exists in $settingsPath"
}

if ($null -eq $companion) {
    $channels += [pscustomobject]@{
        Id = "jackllm-companion"
        DisplayName = "JackLLM Companion"
        UpdateDirectory = "C:\JackLLM\Update\Companion"
        ManagedProcessName = "JackLLMCompanion"
        ManagedExecutablePath = "JackLLMCompanion.exe"
        AutoStartAfterUpdate = $false
    }
    $settings.Channels = $channels
    $settingsChanged = $true
    Write-Host "Added jackllm-companion to $settingsPath"
} else {
    Write-Host "jackllm-companion already exists in $settingsPath"
}

if ($settingsChanged) {
    $backupPath = "$settingsPath.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        Copy-Item -LiteralPath $settingsPath -Destination $backupPath
        Write-Host "Backed up appsettings.json to $backupPath"
    }
    ConvertTo-JsonDepth $settings | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    Write-Host "Saved SocketJack.Update appsettings.json"
} else {
    Write-Host "SocketJack.Update appsettings.json already matches SecureAuthority settings"
}

if (-not $NoRestart) {
    $running = Get-Process -Name "SocketJack.Update" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ((Split-Path -Parent $_.Path) -ieq $install) }

    foreach ($process in $running) {
        Write-Host "Stopping SocketJack.Update pid $($process.Id)"
        Stop-Process -Id $process.Id -Force
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }

    Write-Host "Starting $exePath"
    Start-Process -FilePath $exePath -WorkingDirectory $install
}

Write-Host "Done. Verify local update authority: http://127.0.0.1:8500/SecureAuthority/api/update/channels"
Write-Host "Public access, when needed, is proxied by the Master List website at /SecureAuthority/."
