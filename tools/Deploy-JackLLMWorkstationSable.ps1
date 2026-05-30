param(
    [string]$HostName = "216.235.101.12",
    [int]$Port = 25,
    [string]$User = "wintergrasped",
    [string]$HostKey = "SHA256:qVC8NKNzNX7Zbm9Ce/3zLIYO/Kh8Pa8UiXGk1My4jZU",
    [string]$Configuration = "Release",
    [string]$RemoteNativeCurrent = "/home/wintergrasped/jackllm-workstation-linux/current",
    [string]$RemoteWpfCurrent = "/home/wintergrasped/jackllm-wpf/current",
    [string]$RemoteNativeStorageRoot = "/stor2/JackLLMNative",
    [string]$RemoteStagingRoot = "/stor2/JackLLMDeployStaging",
    [switch]$SkipBuild,
    [switch]$SkipWpf,
    [switch]$NativeOnly,
    [switch]$FrameworkDependentNative,
    [switch]$NoRestart
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptRoot "..")
$NativeProject = Join-Path $RepoRoot "JackLLM.Workstation\JackLLM.Workstation.csproj"
$WpfProject = Join-Path $RepoRoot "JackLLM\JackLLM.csproj"
$NativePublishDir = Join-Path $RepoRoot "JackLLM.Workstation\bin\$Configuration\net8.0\linux-x64\publish"
$WpfPublishDir = Join-Path $RepoRoot "JackLLM\bin\$Configuration\net8.0-windows7.0\win-x64\publish"
$DeployTemp = Join-Path $RepoRoot "artifacts\codex-deploy"

function Find-Tool {
    param(
        [string]$Name,
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path $candidate).Path
        }
    }

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw "Could not find $Name. Install PuTTY or add $Name to PATH."
}

function ConvertTo-PlainText {
    param([Security.SecureString]$SecureString)

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function ConvertTo-BashSingleQuoted {
    param([string]$Value)

    return "'" + $Value.Replace("'", "'""'""'") + "'"
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

function Get-RelativePath {
    param(
        [string]$BasePath,
        [string]$Path
    )

    $baseFull = [IO.Path]::GetFullPath($BasePath).TrimEnd("\", "/") + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    $baseUri = New-Object Uri($baseFull)
    $pathUri = New-Object Uri($pathFull)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString())
}

function Test-ExcludedBuildPath {
    param(
        [string]$RelativePath,
        [string]$TargetKind = ""
    )

    $path = $RelativePath.Replace("\", "/").TrimStart("/")
    $name = [IO.Path]::GetFileName($path)

    if ($TargetKind -eq "linux-x64") {
        if ($path -like "runtimes/*" -and -not ($path -like "runtimes/linux-x64/*")) { return $true }
        if ($path -like "JackONNX/providers/*" -and
            -not ($path -like "JackONNX/providers/cuda/linux-x64/*") -and
            -not ($path -like "JackONNX/providers/cpu/linux-x64/*")) { return $true }
        if ($path -like "libonnxruntime*.so") { return $true }
        if ($path -like "onnxruntime*.dll") { return $true }
    }
    elseif ($TargetKind -eq "wpf-wine") {
        if ($path -like "runtimes/*") { return $true }
        if ($path -like "win-x64/*") { return $true }
        if ($path -like "win-x86/*") { return $true }
        if ($path -like "win-arm64/*") { return $true }
        if ($path -like "JackONNX/providers/*") { return $true }
    }

    if ($path -match "(^|/)appsettings(\.[^/]*)?\.json$") { return $true }
    if ($path -match "(^|/)(settings|user-settings|localsettings)(\.[^/]*)?\.json$") { return $true }
    if ($path -match "(^|/)[^/]+\.settings\.json$") { return $true }
    if ($path -match "(^|/)\.env(\.[^/]*)?$") { return $true }

    switch -Wildcard ($path) {
        "*.env" { return $true }
        "*.user" { return $true }
        "*.suo" { return $true }
        "*.log" { return $true }
        "jackllm-models-location.env" { return $true }
        "SocketJackDatabase.json" { return $true }
        "LlmRuntime.compatibility.json" { return $true }
        "logs/*" { return $true }
        "Log/*" { return $true }
        "Models/*" { return $true }
        "CompleteModels/*" { return $true }
        "ImageModels/*" { return $true }
        "AudioModels/*" { return $true }
        "VideoModels/*" { return $true }
        "JackLLMChat/*" { return $true }
        "SessionFiles/*" { return $true }
        default { return $false }
    }
}

function Get-LocalManifest {
    param(
        [string]$Directory,
        [string]$TargetKind = ""
    )

    if (-not (Test-Path -LiteralPath $Directory)) {
        throw "Local build directory does not exist: $Directory"
    }

    $map = @{}
    Get-ChildItem -LiteralPath $Directory -Recurse -File | ForEach-Object {
        $relative = (Get-RelativePath -BasePath $Directory -Path $_.FullName).Replace("\", "/")
        if (Test-ExcludedBuildPath -RelativePath $relative -TargetKind $TargetKind) {
            return
        }

        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $map[$relative] = [pscustomobject]@{
            RelativePath = $relative
            FullName = $_.FullName
            Hash = $hash
            Length = $_.Length
        }
    }
    return $map
}

function New-RemoteSkipFunction {
    param([string]$TargetKind = "")

    $targetSpecific = ""
    if ($TargetKind -eq "linux-x64") {
        $targetSpecific = @'
  if [[ "$1" == runtimes/* && "$1" != runtimes/linux-x64/* ]]; then return 0; fi
  if [[ "$1" == JackONNX/providers/* && "$1" != JackONNX/providers/cuda/linux-x64/* && "$1" != JackONNX/providers/cpu/linux-x64/* ]]; then return 0; fi
  if [[ "$1" == libonnxruntime*.so || "$1" == onnxruntime*.dll ]]; then return 0; fi
'@
    }
    elseif ($TargetKind -eq "wpf-wine") {
        $targetSpecific = @'
  if [[ "$1" == runtimes/linux-* || "$1" == runtimes/osx-* || "$1" == JackONNX/providers/cuda/linux-* || "$1" == JackONNX/providers/cpu/linux-* ]]; then return 0; fi
  if [[ "$1" == runtimes/* || "$1" == win-x64/* || "$1" == win-x86/* || "$1" == win-arm64/* || "$1" == JackONNX/providers/* ]]; then return 0; fi
'@
    }

    return "should_skip_build_file() {`n" + $targetSpecific + "`n" + @'
  if [[ "$1" == appsettings.json || "$1" == appsettings.*.json || "$1" == settings.json || "$1" == settings.*.json ]]; then return 0; fi
  if [[ "$1" == user-settings.json || "$1" == localsettings.json || "$1" == *.settings.json ]]; then return 0; fi
  if [[ "$1" == *.env || "$1" == .env || "$1" == .env.* || "$1" == *.user || "$1" == *.suo || "$1" == *.log ]]; then return 0; fi
  if [[ "$1" == jackllm-models-location.env || "$1" == SocketJackDatabase.json || "$1" == LlmRuntime.compatibility.json ]]; then return 0; fi
  if [[ "$1" == logs/* || "$1" == Log/* || "$1" == Models/* || "$1" == CompleteModels/* ]]; then return 0; fi
  if [[ "$1" == ImageModels/* || "$1" == AudioModels/* || "$1" == VideoModels/* || "$1" == JackLLMChat/* || "$1" == SessionFiles/* ]]; then return 0; fi
  return 1
}
'@
}

function New-RemoteManifestScript {
    param(
        [string]$RemoteDirectory,
        [string]$TargetKind = ""
    )

    $quotedDirectory = ConvertTo-BashSingleQuoted $RemoteDirectory
    return @"
set -euo pipefail
target=$quotedDirectory
if [ ! -d "`$target" ]; then
  exit 0
fi
cd "`$target"
$(New-RemoteSkipFunction -TargetKind $TargetKind)
find . -type f -print0 | while IFS= read -r -d '' file; do
  rel="`${file#./}"
  if should_skip_build_file "`$rel"; then
    continue
  fi
  hash="`$(sha256sum "`$rel" | awk '{print `$1}')"
  size="`$(stat -c '%s' "`$rel")"
  printf '%s\t%s\t%s\n' "`$hash" "`$size" "`$rel"
done
"@
}

function Get-RemoteManifest {
    param(
        [string]$RemoteDirectory,
        [string]$TargetKind = ""
    )

    $lines = Invoke-RemoteBash -Script (New-RemoteManifestScript -RemoteDirectory $RemoteDirectory -TargetKind $TargetKind)
    $map = @{}
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $parts = $line -split "`t", 3
        if ($parts.Count -eq 3) {
            $map[$parts[2]] = [pscustomobject]@{
                Hash = $parts[0].ToLowerInvariant()
                Length = [int64]$parts[1]
                RelativePath = $parts[2]
            }
        }
    }
    return $map
}

function Invoke-RemoteBash {
    param(
        [string]$Script,
        [switch]$AllowFailure
    )

    $bytes = [Text.Encoding]::UTF8.GetBytes($Script)
    $encoded = [Convert]::ToBase64String($bytes)
    $command = "printf '%s' '$encoded' | base64 -d | bash"
    $args = @("-ssh", "-P", "$Port")
    if (-not [string]::IsNullOrWhiteSpace($HostKey)) {
        $args += @("-hostkey", $HostKey)
    }
    $args += @("-batch", "-pw", $script:SshPassword, "$User@$HostName", $command)

    $output = & $script:PlinkPath @args 2>&1
    $exit = $LASTEXITCODE
    if ($exit -ne 0 -and -not $AllowFailure) {
        $text = ($output | Out-String).Trim()
        throw "Remote command failed with exit code $exit.`n$text"
    }
    return $output
}

function Copy-ToRemote {
    param(
        [string]$LocalPath,
        [string]$RemotePath
    )

    $args = @("-P", "$Port")
    if (-not [string]::IsNullOrWhiteSpace($HostKey)) {
        $args += @("-hostkey", $HostKey)
    }
    $args += @("-batch", "-pw", $script:SshPassword, $LocalPath, "${User}@${HostName}:$RemotePath")
    & $script:PscpPath @args
    if ($LASTEXITCODE -ne 0) {
        throw "Upload failed for $LocalPath."
    }
}

function Build-Projects {
    if ($SkipBuild) {
        Write-Host "==> Skipping local build."
        return
    }

    $selfContained = if ($FrameworkDependentNative) { "false" } else { "true" }
    Invoke-External -FilePath "dotnet" -Arguments @(
        "publish",
        $NativeProject,
        "--configuration", $Configuration,
        "--runtime", "linux-x64",
        "--self-contained", $selfContained,
        "--nologo",
        "-v:minimal"
    ) -Description "Publishing JackLLM.Workstation for linux-x64 (self-contained=$selfContained)"

    if (-not $SkipWpf -and -not $NativeOnly) {
        Invoke-External -FilePath "dotnet" -Arguments @(
            "publish",
            $WpfProject,
            "--configuration", $Configuration,
            "--runtime", "win-x64",
            "--self-contained", "true",
            "--nologo",
            "-v:minimal"
        ) -Description "Publishing JackLLM WPF for Wine (win-x64 self-contained)"
    }
}

function Stop-RemoteWorkstation {
    $script = @'
set -euo pipefail
echo "Stopping JackLLM Workstation on sable..."
if command -v jackllm-workstation-stop >/dev/null 2>&1; then
  jackllm-workstation-stop || true
fi
if [ -x "$HOME/jackllm-wpf/stop-workstation.sh" ]; then
  "$HOME/jackllm-wpf/stop-workstation.sh" || true
else
  if command -v wineserver >/dev/null 2>&1; then
    WINEPREFIX="$HOME/.wine-jackllm-wpf" wineserver -k >/dev/null 2>&1 || true
    WINEPREFIX="$HOME/.wine-jackllm" wineserver -k >/dev/null 2>&1 || true
  fi
  mapfile -t pids < <(ps -u "$USER" -eo pid=,args= | awk '$0 ~ /[J]ackLLM[.]exe|[J]ackLLM[.]Workstation|[k]eep-workstation-linux[.]sh/ { print $1 }')
  if [ "${#pids[@]}" -gt 0 ]; then
    echo "Stopping PIDs: ${pids[*]}"
    kill "${pids[@]}" >/dev/null 2>&1 || true
    sleep 2
    for pid in "${pids[@]}"; do
      if kill -0 "$pid" >/dev/null 2>&1; then
        kill -9 "$pid" >/dev/null 2>&1 || true
      fi
    done
  fi
fi
rm -f "$HOME/jackllm-workstation-linux/native-for-wpf.pid" "$HOME/jackllm-wpf/native-for-wine.pid" "$HOME/jackllm-wpf/wine.pid"
'@
    Invoke-RemoteBash -Script $script | Write-Host
}

function Ensure-RemoteLayout {
    $nativeCurrent = ConvertTo-BashSingleQuoted $RemoteNativeCurrent
    $nativeStorage = ConvertTo-BashSingleQuoted $RemoteNativeStorageRoot
    $stagingRoot = ConvertTo-BashSingleQuoted $RemoteStagingRoot
    $script = @"
set -euo pipefail
native_current=$nativeCurrent
native_storage=$nativeStorage
staging_root=$stagingRoot
mkdir -p "`$native_current/runtimes/linux-x64/native" "`$native_storage/runtimes/linux-x64/native/cuda12" "`$staging_root"
link_heavy_dir() {
  local rel="`$1"
  local source_dir="`$native_current/`$rel"
  local target_dir="`$native_storage/`$rel"
  mkdir -p "`$(dirname "`$source_dir")" "`$target_dir"
  if [ -d "`$source_dir" ] && [ ! -L "`$source_dir" ] && [ -z "`$(find "`$target_dir" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]; then
    cp -a "`$source_dir/." "`$target_dir/" 2>/dev/null || true
  fi
  rm -rf "`$source_dir"
  ln -s "`$target_dir" "`$source_dir"
}
link_heavy_dir "runtimes/linux-x64/native/cuda12"
link_heavy_dir "JackONNX/providers/cuda/linux-x64"
link_heavy_dir "JackONNX/providers/cpu/linux-x64"
df -h / /stor2 2>/dev/null || true
"@
    Invoke-RemoteBash -Script $script | Write-Host
}

function Repair-RemoteCudaCpuLink {
    $nativeCurrent = ConvertTo-BashSingleQuoted $RemoteNativeCurrent
    $script = @"
set -euo pipefail
native_current=$nativeCurrent
cpu_backend=noavx
if grep -qw avx512f /proc/cpuinfo 2>/dev/null && [ -f "`$native_current/runtimes/linux-x64/native/avx512/libggml-cpu.so" ]; then
  cpu_backend=avx512
elif grep -qw avx2 /proc/cpuinfo 2>/dev/null && [ -f "`$native_current/runtimes/linux-x64/native/avx2/libggml-cpu.so" ]; then
  cpu_backend=avx2
elif grep -qw avx /proc/cpuinfo 2>/dev/null && [ -f "`$native_current/runtimes/linux-x64/native/avx/libggml-cpu.so" ]; then
  cpu_backend=avx
fi
if [ -d "`$native_current/runtimes/linux-x64/native/cuda12" ] && [ -f "`$native_current/runtimes/linux-x64/native/`$cpu_backend/libggml-cpu.so" ]; then
  ln -sfn "`$native_current/runtimes/linux-x64/native/`$cpu_backend/libggml-cpu.so" "`$native_current/runtimes/linux-x64/native/cuda12/libggml-cpu.so"
  echo "Linked cuda12/libggml-cpu.so from `$cpu_backend."
fi
"@
    Invoke-RemoteBash -Script $script | Write-Host
}

function Repair-RemoteOnnxLinks {
    $nativeCurrent = ConvertTo-BashSingleQuoted $RemoteNativeCurrent
    $script = @"
set -euo pipefail
native_current=$nativeCurrent
provider_dir="`$native_current/JackONNX/providers/cuda/linux-x64"
if [ -d "`$provider_dir" ]; then
  for lib in libonnxruntime.so libonnxruntime_providers_cuda.so libonnxruntime_providers_shared.so libonnxruntime_providers_tensorrt.so; do
    rm -f "`$native_current/`$lib"
    if [ -f "`$provider_dir/`$lib" ]; then
      ln -s "`$provider_dir/`$lib" "`$native_current/`$lib"
    fi
  done
fi
rm -f "`$native_current"/onnxruntime*.dll
"@
    Invoke-RemoteBash -Script $script | Write-Host
}

function Write-RemoteNativeStartScript {
    $script = @'
set -euo pipefail
cat > "$HOME/jackllm-workstation-linux/start-native-for-wpf.sh" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
BASE="$HOME/jackllm-workstation-linux/current"
WPF_CURRENT="$(readlink -f "$HOME/jackllm-wpf/current" 2>/dev/null || echo "$HOME/jackllm-wpf/current")"
ENV_FILE="$WPF_CURRENT/jackllm-models-location.env"
sync_models_location_env_from_wine_settings() {
  local settings="$HOME/.wine-jackllm-wpf/drive_c/users/$USER/AppData/Local/SocketJack/JackLLM/JackLLM.settings.json"
  if ! command -v python3 >/dev/null 2>&1; then
    return 0
  fi
  python3 - "$settings" "$ENV_FILE" "/stor2" <<'PY'
import json
import os
import sys

settings_path, env_path, fallback = sys.argv[1:4]
location = ""

try:
    with open(settings_path, "r", encoding="utf-8-sig") as handle:
        data = json.load(handle)
    raw = str(data.get("ModelsLocation") or "").strip().strip('"')
    if raw:
        lowered = raw.lower()
        if lowered.startswith("z:\\") or lowered.startswith("z:/"):
            raw = "/" + raw[3:].replace("\\", "/").lstrip("/")
        location = raw
except Exception:
    location = ""

if not location and os.path.isdir(fallback):
    location = fallback

if not location.startswith("/"):
    sys.exit(0)

location = os.path.normpath(location)
models_root = os.path.join(location, "Models")
complete_root = os.path.join(location, "CompleteModels")
os.makedirs(models_root, exist_ok=True)
os.makedirs(complete_root, exist_ok=True)
os.makedirs(os.path.dirname(env_path), exist_ok=True)

def quote(value: str) -> str:
    return "'" + value.replace("'", "'\"'\"'") + "'"

tmp_path = env_path + ".tmp"
with open(tmp_path, "w", encoding="utf-8") as handle:
    handle.write("# Generated by JackLLM Workstation launcher. Sourced by Linux launcher scripts.\n")
    handle.write(f"JACKLLM_MODELS_LOCATION={quote(location)}\n")
    handle.write(f"JACKLLM_MODEL_ROOT={quote(models_root)}\n")
    handle.write(f"JACKLLM_COMPLETE_MODEL_ROOT={quote(complete_root)}\n")
os.replace(tmp_path, env_path)
PY
}
sync_models_location_env_from_wine_settings
if [ -f "$ENV_FILE" ]; then
  set -a
  . "$ENV_FILE"
  set +a
fi
DATA="$HOME/.jackllm-sable"
LOG_DIR="$HOME/jackllm-workstation-linux/logs"
LOG="$LOG_DIR/native-for-wpf.log"
PIDFILE="$HOME/jackllm-workstation-linux/native-for-wpf.pid"
mkdir -p "$LOG_DIR" "$DATA"
MODEL_ROOT="${JACKLLM_MODEL_ROOT:-/stor2/Models}"
COMPLETE_MODEL_ROOT="${JACKLLM_COMPLETE_MODEL_ROOT:-/stor2/CompleteModels}"
mkdir -p "$MODEL_ROOT" "$COMPLETE_MODEL_ROOT"

if curl -fsS http://127.0.0.1:12435/api/v1/runtime/compatibility >/dev/null 2>&1; then
  echo "Native LlmRuntime bridge already listening at http://127.0.0.1:12435"
  exit 0
fi

if [ -f "$PIDFILE" ]; then
  oldpid="$(cat "$PIDFILE" 2>/dev/null || true)"
  if [ -n "$oldpid" ] && kill -0 "$oldpid" 2>/dev/null; then
    echo "Stopping stale native bridge pid=$oldpid" | tee -a "$LOG"
    kill "$oldpid" >/dev/null 2>&1 || true
    sleep 2
  fi
  rm -f "$PIDFILE"
fi

if [ ! -x "$BASE/JackLLM.Workstation" ]; then
  echo "Native bridge executable missing: $BASE/JackLLM.Workstation" | tee -a "$LOG"
  exit 1
fi

echo "[$(date -Is)] starting native LlmRuntime bridge for WPF" | tee -a "$LOG"
echo "BASE=$BASE" >> "$LOG"
echo "WPF_CURRENT=$WPF_CURRENT" >> "$LOG"
echo "MODEL_ROOT=$MODEL_ROOT" >> "$LOG"
echo "COMPLETE_MODEL_ROOT=$COMPLETE_MODEL_ROOT" >> "$LOG"
(
  cd "$BASE" || exit 1
  export JACKLLM_CONTENT_ROOT="$BASE"
  export JACKLLM_DATA_ROOT="$DATA"
  export JACKLLM_MODEL_ROOT="$MODEL_ROOT"
  export JACKLLM_COMPLETE_MODEL_ROOT="$COMPLETE_MODEL_ROOT"
  export JACKLLM_TOOL_ROOT="$WPF_CURRENT/Tools"
  export JACKLLM_AGENT_ROOT="$WPF_CURRENT/Agents"
  export JACKONNX_PYTHON_HOME="$BASE/Python"
  export JACKONNX_AUTO_CUDA_TORCH=1
  export HF_HOME="${HF_HOME:-${JACKLLM_MODELS_LOCATION:-/stor2}/HuggingFace}"
  export HUGGINGFACE_HUB_CACHE="${HUGGINGFACE_HUB_CACHE:-$HF_HOME/hub}"
  export TRANSFORMERS_CACHE="${TRANSFORMERS_CACHE:-$HF_HOME/transformers}"
  export HF_XET_CACHE="${HF_XET_CACHE:-$HF_HOME/xet}"
  mkdir -p "$HF_HOME" "$HUGGINGFACE_HUB_CACHE" "$TRANSFORMERS_CACHE" "$HF_XET_CACHE"
  export JACKLLM_LINUX_GUI=0
  export NVIDIA_SMI_PATH="${NVIDIA_SMI_PATH:-/usr/bin/nvidia-smi}"
  export JACKLLM_NVIDIA_SMI="${JACKLLM_NVIDIA_SMI:-/usr/bin/nvidia-smi}"
  CPU_BACKEND="noavx"
  if grep -qw avx512f /proc/cpuinfo 2>/dev/null; then
    CPU_BACKEND="avx512"
  elif grep -qw avx2 /proc/cpuinfo 2>/dev/null; then
    CPU_BACKEND="avx2"
  elif grep -qw avx /proc/cpuinfo 2>/dev/null; then
    CPU_BACKEND="avx"
  fi
  LLAMA_NATIVE_DIRS="$BASE/runtimes/linux-x64/native/cuda12:$BASE/runtimes/linux-x64/native/$CPU_BACKEND:$BASE/runtimes/linux-x64/native/noavx:$BASE/runtimes/linux-x64/native"
  TORCH_SITE="$BASE/Python/lib/python3.13/site-packages"
  TORCH_SITE64="$BASE/Python/lib64/python3.13/site-packages"
  CUDA_PY_DIRS="$TORCH_SITE/nvidia/cuda_runtime/lib:$TORCH_SITE/nvidia/cublas/lib:$TORCH_SITE/nvidia/cuda_nvrtc/lib:$TORCH_SITE/nvidia/cuda_cupti/lib:$TORCH_SITE64/nvidia/cuda_runtime/lib:$TORCH_SITE64/nvidia/cublas/lib:$TORCH_SITE64/nvidia/cuda_nvrtc/lib:$TORCH_SITE64/nvidia/cuda_cupti/lib"
  export LD_LIBRARY_PATH="$BASE:$LLAMA_NATIVE_DIRS:$CUDA_PY_DIRS:${LD_LIBRARY_PATH:-}"
  exec "$BASE/JackLLM.Workstation" \
    --runtime llmruntime \
    --proxy-port 12434 \
    --copilot-duplicator-port 12433 \
    --chat-port 11436 \
    --runtime-port 12435 \
    --model-root "$MODEL_ROOT" \
    --complete-model-root "$COMPLETE_MODEL_ROOT" \
    --tool-root "$WPF_CURRENT/Tools" \
    --agent-root "$WPF_CURRENT/Agents" \
    --data-root "$DATA" \
    --sql-admin true \
    --verbose
) >> "$LOG" 2>&1 &
pid=$!
echo "$pid" > "$PIDFILE"
echo "Started native bridge pid=$pid" | tee -a "$LOG"

for _ in $(seq 1 120); do
  if curl -fsS http://127.0.0.1:12435/api/v1/runtime/compatibility >/dev/null 2>&1; then
    echo "Native LlmRuntime bridge ready at http://127.0.0.1:12435" | tee -a "$LOG"
    exit 0
  fi
  if ! kill -0 "$pid" 2>/dev/null; then
    echo "Native bridge exited during startup. See $LOG" | tee -a "$LOG"
    tail -n 80 "$LOG" || true
    exit 1
  fi
  sleep 1
done

echo "Native bridge did not answer within 120s. See $LOG" | tee -a "$LOG"
tail -n 80 "$LOG" || true
exit 1
SH
chmod +x "$HOME/jackllm-workstation-linux/start-native-for-wpf.sh"
'@
    Invoke-RemoteBash -Script $script | Write-Host
}

function Sync-BuildDirectory {
    param(
        [string]$Name,
        [string]$LocalDirectory,
        [string]$RemoteDirectory,
        [string]$TargetKind = ""
    )

    Write-Host "==> Diffing $Name"
    $local = Get-LocalManifest -Directory $LocalDirectory -TargetKind $TargetKind
    $remote = Get-RemoteManifest -RemoteDirectory $RemoteDirectory -TargetKind $TargetKind

    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($relative in ($local.Keys | Sort-Object)) {
        if (-not $remote.ContainsKey($relative) -or $remote[$relative].Hash -ne $local[$relative].Hash) {
            $changed.Add($relative) | Out-Null
        }
    }

    if ($changed.Count -eq 0) {
        Write-Host "No changed files for $Name."
        return
    }

    New-Item -ItemType Directory -Force -Path $DeployTemp | Out-Null
    $safeName = ($Name -replace "[^A-Za-z0-9_.-]", "-").ToLowerInvariant()
    $listFile = Join-Path $DeployTemp "$safeName-files.txt"
    $archive = Join-Path $DeployTemp "$safeName-changed.tar.gz"
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }

    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllLines($listFile, [string[]]$changed, $utf8NoBom)

    Write-Host "Packing $($changed.Count) changed file(s) for $Name."
    Invoke-External -FilePath "tar" -Arguments @(
        "-czf", $archive,
        "-C", $LocalDirectory,
        "-T", $listFile
    ) -Description "Creating $Name delta archive"

    $remoteArchive = "$RemoteStagingRoot/$safeName-changed.tar.gz"
    Copy-ToRemote -LocalPath $archive -RemotePath $remoteArchive

    $quotedRemoteDirectory = ConvertTo-BashSingleQuoted $RemoteDirectory
    $quotedRemoteArchive = ConvertTo-BashSingleQuoted $remoteArchive
    $extractScript = @"
set -euo pipefail
target=$quotedRemoteDirectory
archive=$quotedRemoteArchive
mkdir -p "`$target"
tar -xzf "`$archive" -C "`$target"
rm -f "`$archive"
if [ -f "`$target/JackLLM.Workstation" ]; then chmod +x "`$target/JackLLM.Workstation"; fi
find "`$target" -maxdepth 3 -type f \( -name '*.sh' -o -name 'JackLLM.Workstation' \) -exec chmod +x {} \; 2>/dev/null || true
echo "Applied $($changed.Count) changed file(s) to $RemoteDirectory"
"@
    Invoke-RemoteBash -Script $extractScript | Write-Host
}

function Start-RemoteWorkstation {
    if ($NoRestart) {
        Write-Host "==> Skipping remote restart."
        return
    }

    $script = @'
set -euo pipefail
echo "Starting WPF/Wine workstation..."
if command -v jackllm-workstation >/dev/null 2>&1; then
  mkdir -p /var/log/jackllm
  nohup jackllm-workstation >/var/log/jackllm/workstation-launch.log 2>&1 </dev/null &
  echo "Started packaged workstation launcher pid=$!"
elif [ -x "$HOME/jackllm-wpf/start-jackllm-wpf-wine-only.sh" ]; then
  echo "Starting native LlmRuntime bridge..."
  "$HOME/jackllm-workstation-linux/start-native-for-wpf.sh"
  "$HOME/jackllm-wpf/start-jackllm-wpf-wine-only.sh"
elif [ -x "$HOME/jackllm-wpf/start-jackllm-wpf.sh" ]; then
  echo "Starting native LlmRuntime bridge..."
  "$HOME/jackllm-workstation-linux/start-native-for-wpf.sh"
  "$HOME/jackllm-wpf/start-jackllm-wpf.sh"
else
  echo "No WPF start script found under $HOME/jackllm-wpf." >&2
fi
for _ in $(seq 1 30); do
  if curl -fsS http://127.0.0.1:12435/api/v1/runtime/compatibility >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
echo "--- processes ---"
ps -u "$USER" -eo pid,ppid,stat,cmd | grep -E '[J]ackLLM[.]Workstation|[J]ackLLM[.]exe' || true
echo "--- runtime compatibility ---"
curl -fsS http://127.0.0.1:12435/api/v1/runtime/compatibility >/dev/null
echo "LlmRuntime is reachable at http://127.0.0.1:12435"
'@
    Invoke-RemoteBash -Script $script | Write-Host
}

$script:PlinkPath = Find-Tool -Name "plink.exe" -Candidates @("C:\Program Files\PuTTY\plink.exe", "C:\Program Files (x86)\PuTTY\plink.exe")
$script:PscpPath = Find-Tool -Name "pscp.exe" -Candidates @("C:\Program Files\PuTTY\pscp.exe", "C:\Program Files (x86)\PuTTY\pscp.exe")

if ($env:SABLE_SSH_PASSWORD) {
    $script:SshPassword = $env:SABLE_SSH_PASSWORD
}
else {
    $script:SshPassword = ConvertTo-PlainText (Read-Host "Sable SSH password" -AsSecureString)
}

try {
    Build-Projects
    Stop-RemoteWorkstation
    Ensure-RemoteLayout
    Repair-RemoteOnnxLinks
    Write-RemoteNativeStartScript
    Sync-BuildDirectory -Name "native-linux-workstation" -LocalDirectory $NativePublishDir -RemoteDirectory $RemoteNativeCurrent -TargetKind "linux-x64"
    Repair-RemoteCudaCpuLink
    Repair-RemoteOnnxLinks

    if (-not $SkipWpf -and -not $NativeOnly) {
        Sync-BuildDirectory -Name "wpf-wine-workstation" -LocalDirectory $WpfPublishDir -RemoteDirectory $RemoteWpfCurrent -TargetKind "wpf-wine"
    }

    Repair-RemoteCudaCpuLink
    Repair-RemoteOnnxLinks
    Start-RemoteWorkstation
    Write-Host "Done."
}
finally {
    $script:SshPassword = $null
}
