param(
    [string]$Configuration = "Release",
    [string]$Version = "1:26.0.1",
    [string]$OutputPath = "",
    [string]$NativePublishDir = "",
    [string]$WpfPublishDir = ""
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
if ([string]::IsNullOrWhiteSpace($NativePublishDir)) {
    $NativePublishDir = Join-Path $RepoRoot "JackLLM.Workstation\bin\$Configuration\net8.0\linux-x64\publish"
}
if ([string]::IsNullOrWhiteSpace($WpfPublishDir)) {
    $WpfPublishDir = Join-Path $RepoRoot "JackLLM\bin\$Configuration\net8.0-windows7.0\win-x64\publish"
}

$PackageName = "jackllm-workstation"
$Architecture = "amd64"
$SafeVersion = $Version -replace '[:\\/ ]', '_'
$ArtifactsRoot = Join-Path $RepoRoot "artifacts\linux-installer"
$StageRoot = Join-Path $ArtifactsRoot "stage-windows"
$PackageRoot = Join-Path $StageRoot "$($PackageName)_$($SafeVersion)_$Architecture"
$WorkRoot = Join-Path $ArtifactsRoot "deb-work"
$Utf8NoBom = New-Object Text.UTF8Encoding($false)

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

function Copy-DirectoryMirror {
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

function Set-LfText {
    param(
        [string]$Path,
        [string]$Content
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    $text = $Content.Replace("`r`n", "`n").Replace("`r", "`n")
    if (-not $text.EndsWith("`n", [StringComparison]::Ordinal)) {
        $text += "`n"
    }
    [IO.File]::WriteAllText($Path, $text, $Utf8NoBom)
}

function Get-RelativeArchivePath {
    param(
        [string]$BasePath,
        [string]$Path
    )

    $baseFull = [IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    $baseUri = New-Object Uri($baseFull)
    $pathUri = New-Object Uri($pathFull)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).TrimEnd('/')
}

function ConvertTo-MtreeToken {
    param([string]$Value)

    $value = $Value.Replace('\', '/')
    $builder = New-Object Text.StringBuilder
    foreach ($ch in $value.ToCharArray()) {
        $code = [int][char]$ch
        if ($ch -eq ' ') {
            [void]$builder.Append('\040')
        }
        elseif ($ch -eq [char]9) {
            [void]$builder.Append('\011')
        }
        elseif ($ch -eq '\') {
            [void]$builder.Append('\\')
        }
        elseif ($code -lt 32 -or $code -gt 126) {
            [void]$builder.Append(('\' + [Convert]::ToString($code, 8).PadLeft(3, '0')))
        }
        else {
            [void]$builder.Append($ch)
        }
    }
    return $builder.ToString()
}

function Get-ArchiveMode {
    param([string]$RelativePath)

    $path = $RelativePath.Replace('\', '/')
    $leaf = [IO.Path]::GetFileName($path)
    if ($path.StartsWith("usr/bin/", [StringComparison]::OrdinalIgnoreCase)) { return "0755" }
    if ($leaf.EndsWith(".sh", [StringComparison]::OrdinalIgnoreCase)) { return "0755" }
    if ($leaf.Equals("JackLLM.Workstation", [StringComparison]::OrdinalIgnoreCase)) { return "0755" }
    return "0644"
}

function New-MtreeSpec {
    param(
        [string]$Root,
        [string]$SpecPath,
        [switch]$ExcludeDebian
    )

    $lines = New-Object Collections.Generic.List[string]
    $lines.Add("#mtree") | Out-Null
    $lines.Add("/set type=file uid=0 gid=0 uname=root gname=root mode=0644") | Out-Null

    Get-ChildItem -LiteralPath $Root -Recurse -Directory | Sort-Object FullName | ForEach-Object {
        $relative = Get-RelativeArchivePath -BasePath $Root -Path $_.FullName
        if ([string]::IsNullOrWhiteSpace($relative)) { return }
        if ($ExcludeDebian -and ($relative.Equals("DEBIAN", [StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith("DEBIAN/", [StringComparison]::OrdinalIgnoreCase))) { return }
        $lines.Add((ConvertTo-MtreeToken $relative) + " type=dir mode=0755") | Out-Null
    }

    Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = Get-RelativeArchivePath -BasePath $Root -Path $_.FullName
        if ([string]::IsNullOrWhiteSpace($relative)) { return }
        if ($ExcludeDebian -and ($relative.Equals("DEBIAN", [StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith("DEBIAN/", [StringComparison]::OrdinalIgnoreCase))) { return }
        $mode = Get-ArchiveMode -RelativePath $relative
        $content = ConvertTo-MtreeToken $_.FullName
        $lines.Add((ConvertTo-MtreeToken $relative) + " type=file mode=$mode content=$content") | Out-Null
    }

    [IO.File]::WriteAllText($SpecPath, (($lines -join "`n") + "`n"), $Utf8NoBom)
}

function Invoke-Tar {
    param(
        [string]$ArchivePath,
        [string]$SpecPath,
        [ValidateSet("gzip", "xz")]
        [string]$Compression = "gzip"
    )

    $tar = Get-Command "tar.exe" -ErrorAction SilentlyContinue
    if (-not $tar) {
        throw "tar.exe was not found."
    }

    $mode = "-czf"
    if ($Compression -eq "xz") {
        $mode = "-cJf"
    }
    & $tar.Source $mode $ArchivePath --format=pax "@$SpecPath"
    if ($LASTEXITCODE -ne 0) {
        throw "tar.exe failed creating $ArchivePath with exit code $LASTEXITCODE."
    }
}

function Write-ArMemberFromFile {
    param(
        [IO.FileStream]$Stream,
        [string]$Name,
        [string]$Path
    )

    $info = Get-Item -LiteralPath $Path
    $memberName = ($Name + "/").PadRight(16)
    if ($memberName.Length -gt 16) {
        throw "ar member name is too long: $Name"
    }
    $timestamp = ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()).PadRight(12)
    $owner = "0".PadRight(6)
    $group = "0".PadRight(6)
    $mode = "100644".PadRight(8)
    $size = ($info.Length.ToString()).PadRight(10)
    $header = $memberName + $timestamp + $owner + $group + $mode + $size + ([char]0x60) + "`n"
    $headerBytes = [Text.Encoding]::ASCII.GetBytes($header)
    if ($headerBytes.Length -ne 60) {
        throw "Invalid ar header length for $Name."
    }
    $Stream.Write($headerBytes, 0, $headerBytes.Length)

    $input = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $buffer = New-Object byte[] (1024 * 1024)
        while ($true) {
            $read = $input.Read($buffer, 0, $buffer.Length)
            if ($read -le 0) { break }
            $Stream.Write($buffer, 0, $read)
        }
    }
    finally {
        $input.Dispose()
    }

    if (($info.Length % 2) -ne 0) {
        $Stream.WriteByte([byte][char]"`n")
    }
}

function New-ArArchive {
    param(
        [string]$Output,
        [string]$DebianBinary,
        [string]$ControlArchive,
        [string]$DataArchive
    )

    if (Test-Path -LiteralPath $Output) {
        Remove-Item -LiteralPath $Output -Force
    }
    $stream = [IO.File]::Open($Output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $global = [Text.Encoding]::ASCII.GetBytes("!<arch>`n")
        $stream.Write($global, 0, $global.Length)
        Write-ArMemberFromFile -Stream $stream -Name "debian-binary" -Path $DebianBinary
        Write-ArMemberFromFile -Stream $stream -Name ([IO.Path]::GetFileName($ControlArchive)) -Path $ControlArchive
        Write-ArMemberFromFile -Stream $stream -Name ([IO.Path]::GetFileName($DataArchive)) -Path $DataArchive
    }
    finally {
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $NativePublishDir "JackLLM.Workstation"))) {
    throw "Native Linux publish output missing: $NativePublishDir"
}
if (-not (Test-Path -LiteralPath (Join-Path $WpfPublishDir "JackLLM.exe"))) {
    throw "WPF Wine publish output missing: $WpfPublishDir"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $ArtifactsRoot | Out-Null
Reset-Directory -Path $StageRoot -SafeParent $ArtifactsRoot
Reset-Directory -Path $WorkRoot -SafeParent $ArtifactsRoot

$debianDir = Join-Path $PackageRoot "DEBIAN"
$nativeTarget = Join-Path $PackageRoot "opt\jackllm\workstation\native"
$wpfTarget = Join-Path $PackageRoot "opt\jackllm\workstation\wpf"
$usrBin = Join-Path $PackageRoot "usr\bin"
$docDir = Join-Path $PackageRoot "usr\share\doc\$PackageName"
$applicationsDir = Join-Path $PackageRoot "usr\share\applications"
$systemdDir = Join-Path $PackageRoot "usr\lib\systemd\user"
$linuxInstallDir = Join-Path $nativeTarget "install\linux"

@(
    $debianDir,
    $nativeTarget,
    $wpfTarget,
    $usrBin,
    $docDir,
    $applicationsDir,
    $systemdDir,
    (Join-Path $PackageRoot "var\lib\jackllm\Models"),
    (Join-Path $PackageRoot "var\lib\jackllm\CompleteModels"),
    (Join-Path $PackageRoot "var\lib\jackllm\Tools"),
    (Join-Path $PackageRoot "var\lib\jackllm\Agents"),
    (Join-Path $PackageRoot "var\lib\jackllm\Python"),
    (Join-Path $PackageRoot "var\log\jackllm"),
    $linuxInstallDir
) | ForEach-Object { New-Item -ItemType Directory -Force -Path $_ | Out-Null }

Copy-DirectoryMirror -Source $NativePublishDir -Destination $nativeTarget
Copy-DirectoryMirror -Source $WpfPublishDir -Destination $wpfTarget
Copy-Item -LiteralPath (Join-Path $RepoRoot "tools\linux\install-jackllm-cuda-pytorch.sh") -Destination (Join-Path $linuxInstallDir "install-jackllm-cuda-pytorch.sh") -Force

Set-LfText -Path (Join-Path $usrBin "jackllm-workstation") -Content @'
#!/usr/bin/env bash
set -Eeuo pipefail
APP_ROOT="/opt/jackllm/workstation"
WPF_ROOT="$APP_ROOT/wpf"
load_models_location_hint() {
  for hint in "${JACKLLM_MODELS_LOCATION_ENV_PATH:-}" "$HOME/.config/jackllm/jackllm-models-location.env" "/var/lib/jackllm/jackllm-models-location.env" "$WPF_ROOT/jackllm-models-location.env"; do
    [ -n "$hint" ] || continue
    [ -f "$hint" ] || continue
    set -a
    . "$hint"
    set +a
    break
  done
}
load_models_location_hint
export WINEPREFIX="${WINEPREFIX:-$HOME/.wine-jackllm-wpf}"
export WINEARCH="${WINEARCH:-win64}"
export WINEDEBUG="${WINEDEBUG:-fixme-all}"
export JACKLLM_WINE_SAFE_WPF="${JACKLLM_WINE_SAFE_WPF:-1}"
export JACKLLM_ENABLE_LINUX_NATIVE_BACKEND="${JACKLLM_ENABLE_LINUX_NATIVE_BACKEND:-1}"
export JACKLLM_NATIVE_BACKEND_PATH="${JACKLLM_NATIVE_BACKEND_PATH:-$APP_ROOT/native/JackLLM.Workstation}"
export JACKLLM_NATIVE_BACKEND_LOG="${JACKLLM_NATIVE_BACKEND_LOG:-/var/log/jackllm/native-backend.log}"
export JACKLLM_MODELS_LOCATION="${JACKLLM_MODELS_LOCATION:-/var/lib/jackllm}"
export JACKLLM_DATA_ROOT="${JACKLLM_DATA_ROOT:-/var/lib/jackllm}"
export JACKLLM_MODEL_ROOT="${JACKLLM_MODEL_ROOT:-$JACKLLM_MODELS_LOCATION/Models}"
export JACKLLM_COMPLETE_MODEL_ROOT="${JACKLLM_COMPLETE_MODEL_ROOT:-$JACKLLM_MODELS_LOCATION/CompleteModels}"
export JACKLLM_TOOL_ROOT="${JACKLLM_TOOL_ROOT:-$JACKLLM_DATA_ROOT/Tools}"
export JACKLLM_AGENT_ROOT="${JACKLLM_AGENT_ROOT:-$JACKLLM_DATA_ROOT/Agents}"
export JACKONNX_PYTHON_HOME="${JACKONNX_PYTHON_HOME:-$JACKLLM_DATA_ROOT/Python}"
export JACKONNX_PYTHON="${JACKONNX_PYTHON:-$JACKONNX_PYTHON_HOME/bin/python3}"
export JACKLLM_MODELS_MANAGER_RUNTIME_URL="${JACKLLM_MODELS_MANAGER_RUNTIME_URL:-http://127.0.0.1:12435}"
export JACKLLM_LLMRUNTIME_URL="${JACKLLM_LLMRUNTIME_URL:-http://127.0.0.1:12435}"
export JACKLLM_EXTERNAL_BROWSER="${JACKLLM_EXTERNAL_BROWSER:-1}"
export JACKONNX_AUTO_CUDA_TORCH="${JACKONNX_AUTO_CUDA_TORCH:-0}"
export JACKLLM_RESTORE_LOADED_MODELS_ON_STARTUP="${JACKLLM_RESTORE_LOADED_MODELS_ON_STARTUP:-0}"
export NVIDIA_SMI_PATH="${NVIDIA_SMI_PATH:-/usr/bin/nvidia-smi}"
export JACKLLM_NVIDIA_SMI="${JACKLLM_NVIDIA_SMI:-/usr/bin/nvidia-smi}"
mkdir -p "$JACKLLM_MODELS_LOCATION" "$JACKLLM_MODEL_ROOT" "$JACKLLM_COMPLETE_MODEL_ROOT" "$JACKLLM_TOOL_ROOT" "$JACKLLM_AGENT_ROOT" "$(dirname "$JACKONNX_PYTHON_HOME")" "$HOME/.local/state/jackllm"
WINE_BIN="${WINE_BIN:-}"
if [ -z "$WINE_BIN" ]; then
  WINE_BIN="$(command -v wine 2>/dev/null || command -v wine64 2>/dev/null || true)"
fi
if [ -z "$WINE_BIN" ]; then
  echo "wine or wine64 was not found. Install Wine before launching JackLLM Workstation." >&2
  exit 1
fi
if [ "${JACKLLM_PRESTART_NATIVE_BACKEND:-1}" != "0" ] && [ "${JACKLLM_DISABLE_LINUX_NATIVE_BACKEND:-0}" != "1" ]; then
  if ! curl -fsS --max-time 2 "$JACKLLM_LLMRUNTIME_URL/api/v1/runtime/compatibility" >/dev/null 2>&1; then
    mkdir -p "$(dirname "$JACKLLM_NATIVE_BACKEND_LOG")"
    nohup env JACKLLM_CHAT_PORT="${JACKLLM_NATIVE_CHAT_PORT:-12437}" jackllm-workstation-native >> "$JACKLLM_NATIVE_BACKEND_LOG" 2>&1 < /dev/null &
    for _ in $(seq 1 45); do
      if curl -fsS --max-time 2 "$JACKLLM_LLMRUNTIME_URL/api/v1/runtime/compatibility" >/dev/null 2>&1; then
        break
      fi
      sleep 1
    done
  fi
fi
cd "$WPF_ROOT"
exec "$WINE_BIN" "$WPF_ROOT/JackLLM.exe" "$@"
'@

Set-LfText -Path (Join-Path $usrBin "jackllm-workstation-native") -Content @'
#!/usr/bin/env bash
set -Eeuo pipefail
BASE="/opt/jackllm/workstation/native"
WPF_ROOT="/opt/jackllm/workstation/wpf"
load_models_location_hint() {
  for hint in "${JACKLLM_MODELS_LOCATION_ENV_PATH:-}" "$HOME/.config/jackllm/jackllm-models-location.env" "/var/lib/jackllm/jackllm-models-location.env" "$WPF_ROOT/jackllm-models-location.env"; do
    [ -n "$hint" ] || continue
    [ -f "$hint" ] || continue
    set -a
    . "$hint"
    set +a
    break
  done
}
load_models_location_hint
DATA="${JACKLLM_DATA_ROOT:-/var/lib/jackllm}"
LOG="${JACKLLM_NATIVE_BACKEND_LOG:-/var/log/jackllm/native-backend.log}"
MODELS_LOCATION="${JACKLLM_MODELS_LOCATION:-$DATA}"
MODEL_ROOT="${JACKLLM_MODEL_ROOT:-$MODELS_LOCATION/Models}"
COMPLETE_MODEL_ROOT="${JACKLLM_COMPLETE_MODEL_ROOT:-$MODELS_LOCATION/CompleteModels}"
TOOL_ROOT="${JACKLLM_TOOL_ROOT:-$DATA/Tools}"
AGENT_ROOT="${JACKLLM_AGENT_ROOT:-$DATA/Agents}"
PYTHON_HOME="${JACKONNX_PYTHON_HOME:-$DATA/Python}"
PYTHON_EXE="${JACKONNX_PYTHON:-$PYTHON_HOME/bin/python3}"
CHAT_PORT="${JACKLLM_CHAT_PORT:-11436}"
mkdir -p "$DATA" "$MODEL_ROOT" "$COMPLETE_MODEL_ROOT" "$TOOL_ROOT" "$AGENT_ROOT" "$(dirname "$PYTHON_HOME")" "$(dirname "$LOG")"
CPU_BACKEND=noavx
if grep -qw avx512f /proc/cpuinfo 2>/dev/null && [ -f "$BASE/runtimes/linux-x64/native/avx512/libggml-cpu.so" ]; then
  CPU_BACKEND=avx512
elif grep -qw avx2 /proc/cpuinfo 2>/dev/null && [ -f "$BASE/runtimes/linux-x64/native/avx2/libggml-cpu.so" ]; then
  CPU_BACKEND=avx2
elif grep -qw avx /proc/cpuinfo 2>/dev/null && [ -f "$BASE/runtimes/linux-x64/native/avx/libggml-cpu.so" ]; then
  CPU_BACKEND=avx
fi
LLAMA_NATIVE_DIRS="$BASE/runtimes/linux-x64/native/cuda12:$BASE/runtimes/linux-x64/native/$CPU_BACKEND:$BASE/runtimes/linux-x64/native/noavx:$BASE/runtimes/linux-x64/native"
CUDA_PY_DIRS=""
for TORCH_SITE in "$PYTHON_HOME"/lib/python*/site-packages "$PYTHON_HOME"/lib64/python*/site-packages; do
  [ -d "$TORCH_SITE" ] || continue
  CUDA_PY_DIRS="$CUDA_PY_DIRS:$TORCH_SITE/torch/lib:$TORCH_SITE/nvidia/cuda_runtime/lib:$TORCH_SITE/nvidia/cublas/lib:$TORCH_SITE/nvidia/cuda_nvrtc/lib:$TORCH_SITE/nvidia/cuda_cupti/lib:$TORCH_SITE/nvidia/cudnn/lib:$TORCH_SITE/nvidia/cufft/lib:$TORCH_SITE/nvidia/curand/lib:$TORCH_SITE/nvidia/cusolver/lib:$TORCH_SITE/nvidia/cusparse/lib:$TORCH_SITE/nvidia/nccl/lib:$TORCH_SITE/nvidia/nvjitlink/lib:$TORCH_SITE/nvidia/nvtx/lib"
done
export JACKLLM_CONTENT_ROOT="$BASE"
export JACKLLM_DATA_ROOT="$DATA"
export JACKLLM_MODELS_LOCATION="$MODELS_LOCATION"
export JACKLLM_MODEL_ROOT="$MODEL_ROOT"
export JACKLLM_COMPLETE_MODEL_ROOT="$COMPLETE_MODEL_ROOT"
export JACKLLM_TOOL_ROOT="$TOOL_ROOT"
export JACKLLM_AGENT_ROOT="$AGENT_ROOT"
export JACKONNX_PYTHON_HOME="$PYTHON_HOME"
export JACKONNX_PYTHON="$PYTHON_EXE"
export JACKONNX_AUTO_CUDA_TORCH="${JACKONNX_AUTO_CUDA_TORCH:-0}"
RESTORE_LOADED_MODELS="${JACKLLM_RESTORE_LOADED_MODELS_ON_STARTUP:-1}"
RESTORE_ARGS=()
case "${RESTORE_LOADED_MODELS,,}" in
  0|false|no|off) RESTORE_ARGS+=(--no-restore-loaded-models) ;;
esac
export JACKLLM_LINUX_GUI=0
export NVIDIA_SMI_PATH="${NVIDIA_SMI_PATH:-/usr/bin/nvidia-smi}"
export JACKLLM_NVIDIA_SMI="${JACKLLM_NVIDIA_SMI:-/usr/bin/nvidia-smi}"
export LD_LIBRARY_PATH="$BASE:$LLAMA_NATIVE_DIRS:$CUDA_PY_DIRS:${LD_LIBRARY_PATH:-}"
cd "$BASE"
exec "$BASE/JackLLM.Workstation" \
  --runtime llmruntime \
  --proxy-port 12434 \
  --copilot-duplicator-port 12433 \
  --chat-port "$CHAT_PORT" \
  --runtime-port 12435 \
  --model-root "$MODEL_ROOT" \
  --complete-model-root "$COMPLETE_MODEL_ROOT" \
  --tool-root "$TOOL_ROOT" \
  --agent-root "$AGENT_ROOT" \
  --data-root "$DATA" \
  --sql-admin true \
  "${RESTORE_ARGS[@]}" \
  --verbose "$@"
'@

Set-LfText -Path (Join-Path $usrBin "jackllm-workstation-stop") -Content @'
#!/usr/bin/env bash
set -Eeuo pipefail
if command -v wineserver >/dev/null 2>&1; then
  WINEPREFIX="${WINEPREFIX:-$HOME/.wine-jackllm-wpf}" wineserver -k >/dev/null 2>&1 || true
fi
mapfile -t pids < <(ps -u "$USER" -eo pid=,args= | awk '$0 ~ /[J]ackLLM[.]exe|[J]ackLLM[.]Workstation|[j]ackllm-workstation-native/ { print $1 }')
if [ "${#pids[@]}" -gt 0 ]; then
  kill "${pids[@]}" >/dev/null 2>&1 || true
  sleep 2
  for pid in "${pids[@]}"; do
    if kill -0 "$pid" >/dev/null 2>&1; then
      kill -9 "$pid" >/dev/null 2>&1 || true
    fi
  done
fi
'@

Set-LfText -Path (Join-Path $usrBin "jackllm-workstation-start-shortcut") -Content @'
#!/usr/bin/env bash
set -Eeuo pipefail
export JACKLLM_PRESTART_NATIVE_BACKEND=1
export JACKLLM_ENABLE_LINUX_NATIVE_BACKEND=1
export JACKLLM_RESTORE_LOADED_MODELS_ON_STARTUP="${JACKLLM_RESTORE_LOADED_MODELS_ON_STARTUP:-0}"
runtime_url="${JACKLLM_LLMRUNTIME_URL:-http://127.0.0.1:12435}"
native_log="${JACKLLM_NATIVE_BACKEND_LOG:-/var/log/jackllm/native-backend.log}"
if ! curl -fsS --max-time 2 "$runtime_url/api/v1/runtime/compatibility" >/dev/null 2>&1; then
  mkdir -p "$(dirname "$native_log")"
  nohup env JACKLLM_CHAT_PORT="${JACKLLM_NATIVE_CHAT_PORT:-12437}" /usr/bin/jackllm-workstation-native >> "$native_log" 2>&1 < /dev/null &
  for _ in $(seq 1 45); do
    if curl -fsS --max-time 2 "$runtime_url/api/v1/runtime/compatibility" >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done
fi
exec /usr/bin/jackllm-workstation "$@"
'@

Set-LfText -Path (Join-Path $usrBin "jackllm-workstation-stop-shortcut") -Content @'
#!/usr/bin/env bash
set -Eeuo pipefail
/usr/bin/jackllm-workstation-stop "$@" || true
for _ in $(seq 1 8); do
  if ! ps -u "$USER" -eo args= | grep -Eq '[J]ackLLM[.]exe|[J]ackLLM[.]Workstation|[j]ackllm-workstation-native'; then
    exit 0
  fi
  sleep 1
done
mapfile -t pids < <(ps -u "$USER" -eo pid=,args= | awk '$0 ~ /[J]ackLLM[.]exe|[J]ackLLM[.]Workstation|[j]ackllm-workstation-native/ { print $1 }')
if [ "${#pids[@]}" -gt 0 ]; then
  kill -9 "${pids[@]}" >/dev/null 2>&1 || true
fi
'@

Set-LfText -Path (Join-Path $usrBin "jackllm-workstation-info") -Content @'
#!/usr/bin/env bash
set -Eeuo pipefail
cat <<'INFO'
JackLLM Workstation Linux

Run the WPF UI through Wine:
  jackllm-workstation

Browser workstation URL:
  http://127.0.0.1:11436/

Stop the WPF UI and native backend:
  jackllm-workstation-stop

Run only the hidden native backend:
  jackllm-workstation-native

Install locations:
  WPF app:        /opt/jackllm/workstation/wpf/JackLLM.exe
  Native backend: /opt/jackllm/workstation/native/JackLLM.Workstation
  App root:       /opt/jackllm/workstation

Mutable data:
  Models location env: /var/lib/jackllm/jackllm-models-location.env
  Models root:         /var/lib/jackllm/Models
  CompleteModels root: /var/lib/jackllm/CompleteModels
  Tools root:          /var/lib/jackllm/Tools
  Agents root:         /var/lib/jackllm/Agents
  Python root:         /var/lib/jackllm/Python
  Logs:                /var/log/jackllm
  Install status:      /var/lib/jackllm/install-status.json

Desktop menu entries:
  JackLLM Workstation
  STOP JACKLLM WORKSTATION
  JackLLM Workstation Info

If a desktop menu does not refresh immediately, run:
  gtk-launch jackllm-workstation
INFO
'@

Set-LfText -Path (Join-Path $usrBin "llmworkstation") -Content "#!/usr/bin/env bash`nexec /usr/bin/jackllm-workstation ""`$@""`n"
Set-LfText -Path (Join-Path $usrBin "llm-workstation") -Content "#!/usr/bin/env bash`nexec /usr/bin/jackllm-workstation ""`$@""`n"
Set-LfText -Path (Join-Path $usrBin "llmworkstation-info") -Content "#!/usr/bin/env bash`nexec /usr/bin/jackllm-workstation-info ""`$@""`n"

Set-LfText -Path (Join-Path $docDir "README.txt") -Content @'
JackLLM Workstation Linux
=========================

Run:
  jackllm-workstation

Browser workstation URL:
  http://127.0.0.1:11436/

Stop:
  jackllm-workstation-stop

Show this install layout:
  jackllm-workstation-info

Install locations:
  WPF app:        /opt/jackllm/workstation/wpf/JackLLM.exe
  Native backend: /opt/jackllm/workstation/native/JackLLM.Workstation
  App root:       /opt/jackllm/workstation

Mutable data:
  Models root:         /var/lib/jackllm/Models
  CompleteModels root: /var/lib/jackllm/CompleteModels
  Tools root:          /var/lib/jackllm/Tools
  Agents root:         /var/lib/jackllm/Agents
  Python root:         /var/lib/jackllm/Python
  Logs:                /var/log/jackllm
  Install status:      /var/lib/jackllm/install-status.json

The visible Linux GUI is the Wine-hosted WPF app. The launcher starts the
Linux-native backend for Wine sessions before opening WPF, and WPF monitors
the same runtime URL.
'@

Set-LfText -Path (Join-Path $applicationsDir "jackllm-workstation.desktop") -Content @'
[Desktop Entry]
Type=Application
Name=JackLLM Workstation
Comment=Start the Linux native backend and open the JackLLM Workstation WPF UI through Wine
Exec=/usr/bin/jackllm-workstation-start-shortcut
Icon=wine
Terminal=false
Categories=Development;Utility;
StartupNotify=true
'@

Set-LfText -Path (Join-Path $applicationsDir "jackllm-workstation-stop.desktop") -Content @'
[Desktop Entry]
Type=Application
Name=STOP JACKLLM WORKSTATION
Comment=Stop JackLLM Workstation Wine and the Linux native backend bridge
Exec=/usr/bin/jackllm-workstation-stop-shortcut
Icon=process-stop
Terminal=false
Categories=Development;Utility;
StartupNotify=false
'@

Set-LfText -Path (Join-Path $applicationsDir "jackllm-workstation-info.desktop") -Content @'
[Desktop Entry]
Type=Application
Name=JackLLM Workstation Info
Comment=Show JackLLM Workstation Linux install paths and commands
Exec=/bin/sh -lc '/usr/bin/jackllm-workstation-info; printf "\nPress Enter to close..."; read _'
Icon=help-about
Terminal=true
Categories=Development;Utility;
StartupNotify=false
'@

Set-LfText -Path (Join-Path $systemdDir "jackllm-workstation-native.service") -Content @'
[Unit]
Description=JackLLM Workstation native backend
After=network-online.target

[Service]
Type=simple
ExecStart=/usr/bin/jackllm-workstation-native
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
'@

$installedBytes = (Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | Where-Object { -not $_.FullName.StartsWith($debianDir, [StringComparison]::OrdinalIgnoreCase) } | Measure-Object -Property Length -Sum).Sum
if ($null -eq $installedBytes) { $installedBytes = 0 }
$installedSize = [Math]::Max(1, [Math]::Ceiling([double]$installedBytes / 1024.0))

Set-LfText -Path (Join-Path $debianDir "control") -Content @"
Package: $PackageName
Version: $Version
Section: utils
Priority: optional
Architecture: $Architecture
Installed-Size: $installedSize
Maintainer: SocketJack <support@socketjack.com>
Depends: bash, ca-certificates, curl, python3, python3-venv, python3-pip, wine64 | wine, libgomp1, libstdc++6
Recommends: xdg-utils, desktop-file-utils, nvidia-utils-550 | nvidia-utils | nvidia-driver, libcudart12, libcublas12, libcublaslt12, libcufft11, libcudnn9-cuda-12
Description: JackLLM Workstation for Linux
 Wine-hosted JackLLM WPF Workstation with a Linux-native LlmRuntime backend.
"@

Set-LfText -Path (Join-Path $debianDir "postinst") -Content @'
#!/usr/bin/env bash
set -e

install_desktop_shortcut_set() {
  target_home="$1"
  target_owner="${2:-}"
  [ -n "$target_home" ] || return 0
  [ -d "$target_home" ] || return 0

  desktop_dir="$target_home/Desktop"
  mkdir -p "$desktop_dir"
  for launcher in jackllm-workstation jackllm-workstation-stop jackllm-workstation-info; do
    source_file="/usr/share/applications/$launcher.desktop"
    target_file="$desktop_dir/$launcher.desktop"
    if [ -f "$source_file" ]; then
      cp -f "$source_file" "$target_file"
      chmod 0755 "$target_file"
      if [ -n "$target_owner" ] && id "$target_owner" >/dev/null 2>&1; then
        chown "$target_owner:$target_owner" "$desktop_dir" "$target_file" 2>/dev/null || true
      fi
    fi
  done
}

install_desktop_shortcuts() {
  install_desktop_shortcut_set "/etc/skel" ""

  if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ] && id "$SUDO_USER" >/dev/null 2>&1; then
    sudo_home="$(getent passwd "$SUDO_USER" | cut -d: -f6)"
    install_desktop_shortcut_set "$sudo_home" "$SUDO_USER"
  fi

  if [ -d /home ]; then
    for home_dir in /home/*; do
      [ -d "$home_dir" ] || continue
      user_name="$(basename "$home_dir")"
      if id "$user_name" >/dev/null 2>&1; then
        uid="$(id -u "$user_name" 2>/dev/null || echo 0)"
        if [ "$uid" -ge 1000 ]; then
          install_desktop_shortcut_set "$home_dir" "$user_name"
        fi
      fi
    done
  fi

  chmod 0755 /etc/skel/Desktop/*.desktop 2>/dev/null || true
}

mkdir -p /var/lib/jackllm/Models /var/lib/jackllm/CompleteModels /var/lib/jackllm/Tools /var/lib/jackllm/Agents /var/lib/jackllm/Python /var/log/jackllm
chmod 0777 /var/lib/jackllm /var/lib/jackllm/Models /var/lib/jackllm/CompleteModels /var/lib/jackllm/Tools /var/lib/jackllm/Agents /var/lib/jackllm/Python /var/log/jackllm || true
if [ ! -f /var/lib/jackllm/jackllm-models-location.env ]; then
  cat > /var/lib/jackllm/jackllm-models-location.env <<'EOF'
# Generated by JackLLM Workstation installer. The WPF app rewrites this when Models Location changes.
JACKLLM_MODELS_LOCATION='/var/lib/jackllm'
JACKLLM_MODEL_ROOT='/var/lib/jackllm/Models'
JACKLLM_COMPLETE_MODEL_ROOT='/var/lib/jackllm/CompleteModels'
EOF
fi
chmod 0666 /var/lib/jackllm/jackllm-models-location.env || true
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
fi
install_desktop_shortcuts
nvidia_smi=""
if command -v nvidia-smi >/dev/null 2>&1; then
  nvidia_smi="$(command -v nvidia-smi)"
fi
cuda_runtime=false
if ldconfig -p 2>/dev/null | grep -Eq 'libcudart|libcuda\.so'; then
  cuda_runtime=true
elif find /usr/local/cuda* -name 'libcudart.so*' -print -quit 2>/dev/null | grep -q .; then
  cuda_runtime=true
fi
cat > /var/lib/jackllm/install-status.json <<EOF
{
  "installedAtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "appRoot": "/opt/jackllm/workstation",
  "wpfApp": "/opt/jackllm/workstation/wpf/JackLLM.exe",
  "nativeBackend": "/opt/jackllm/workstation/native/JackLLM.Workstation",
  "runCommand": "jackllm-workstation",
  "stopCommand": "jackllm-workstation-stop",
  "infoCommand": "jackllm-workstation-info",
  "desktopShortcuts": [
    "jackllm-workstation.desktop",
    "jackllm-workstation-stop.desktop",
    "jackllm-workstation-info.desktop"
  ],
  "browserUrl": "http://127.0.0.1:11436/",
  "runtimeUrl": "http://127.0.0.1:12435/",
  "modelsRoot": "/var/lib/jackllm/Models",
  "completeModelsRoot": "/var/lib/jackllm/CompleteModels",
  "toolsRoot": "/var/lib/jackllm/Tools",
  "agentsRoot": "/var/lib/jackllm/Agents",
  "pythonRoot": "/var/lib/jackllm/Python",
  "pythonExecutable": "/var/lib/jackllm/Python/bin/python3",
  "logRoot": "/var/log/jackllm",
  "nvidiaSmi": "$nvidia_smi",
  "cudaRuntimeDetected": $cuda_runtime,
  "cudaInstallPolicy": "recommended-packages-during-apt-install-when-available",
  "pytorchInstall": "user-confirmed-in-workstation"
}
EOF
chmod 0666 /var/lib/jackllm/install-status.json || true
cat <<'MSG'

JackLLM Workstation Linux installed.

Run:
  jackllm-workstation

Stop:
  jackllm-workstation-stop

Show install paths:
  jackllm-workstation-info

Installed to:
  /opt/jackllm/workstation

Models:
  /var/lib/jackllm/Models
  /var/lib/jackllm/CompleteModels
  /var/lib/jackllm/Tools
  /var/lib/jackllm/Agents
  /var/lib/jackllm/Python

Logs:
  /var/log/jackllm

Desktop menu entries:
  JackLLM Workstation
  STOP JACKLLM WORKSTATION
  JackLLM Workstation Info

Desktop shortcuts:
  ~/Desktop/jackllm-workstation.desktop
  ~/Desktop/jackllm-workstation-stop.desktop
  ~/Desktop/jackllm-workstation-info.desktop

MSG
exit 0
'@

Set-LfText -Path (Join-Path $debianDir "prerm") -Content @'
#!/usr/bin/env bash
set -e
if command -v jackllm-workstation-stop >/dev/null 2>&1; then
  jackllm-workstation-stop >/dev/null 2>&1 || true
fi
exit 0
'@

Set-LfText -Path (Join-Path $debianDir "postrm") -Content @'
#!/usr/bin/env bash
set -e
remove_desktop_shortcut_set() {
  target_home="$1"
  [ -n "$target_home" ] || return 0
  [ -d "$target_home/Desktop" ] || return 0
  rm -f \
    "$target_home/Desktop/jackllm-workstation.desktop" \
    "$target_home/Desktop/jackllm-workstation-stop.desktop" \
    "$target_home/Desktop/jackllm-workstation-info.desktop"
}

if [ "${1:-}" = "remove" ] || [ "${1:-}" = "purge" ]; then
  remove_desktop_shortcut_set "/etc/skel"
  if [ -d /home ]; then
    for home_dir in /home/*; do
      [ -d "$home_dir" ] || continue
      remove_desktop_shortcut_set "$home_dir"
    done
  fi
fi

if [ "${1:-}" = "purge" ]; then
  rm -rf /var/lib/jackllm /var/log/jackllm
fi
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
fi
exit 0
'@

$debianBinary = Join-Path $WorkRoot "debian-binary"
$controlSpec = Join-Path $WorkRoot "control.mtree"
$dataSpec = Join-Path $WorkRoot "data.mtree"
$controlArchive = Join-Path $WorkRoot "control.tar.gz"
$dataArchive = Join-Path $WorkRoot "data.tar.xz"

Set-LfText -Path $debianBinary -Content "2.0`n"
New-MtreeSpec -Root $debianDir -SpecPath $controlSpec
New-MtreeSpec -Root $PackageRoot -SpecPath $dataSpec -ExcludeDebian
Invoke-Tar -ArchivePath $controlArchive -SpecPath $controlSpec -Compression gzip
Invoke-Tar -ArchivePath $dataArchive -SpecPath $dataSpec -Compression xz
New-ArArchive -Output $OutputPath -DebianBinary $debianBinary -ControlArchive $controlArchive -DataArchive $dataArchive

$info = Get-Item -LiteralPath $OutputPath
Write-Host "Built $($info.FullName) ($($info.Length) bytes)"
