#!/usr/bin/env bash
set -Eeuo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
VERSION="${JACKLLM_WORKSTATION_VERSION:-1:26.0.1}"
OUTPUT_DIR=""
SKIP_PUBLISH=0
NATIVE_PUBLISH_OVERRIDE=""
WPF_PUBLISH_OVERRIDE=""
PACKAGE_FILE_NAME=""

usage() {
  cat <<'EOF'
Usage: package-jackllm-workstation-deb.sh [options]

Builds a Debian package for JackLLM Workstation Linux.

Options:
  --configuration VALUE   Build configuration. Default: Release.
  --version VALUE         Debian package version. Default: 1:26.0.1.
  --output PATH           Output directory. Default: artifacts/linux-installer.
  --native-publish PATH   Existing linux-x64 JackLLM.Workstation publish folder.
  --wpf-publish PATH      Existing win-x64 JackLLM WPF publish folder.
  --package-file-name     Output .deb file name. Default: jackllm-workstation_<version>_amd64.deb.
  --skip-publish          Reuse existing dotnet publish outputs.
  -h, --help              Show this help.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --configuration)
      CONFIGURATION="${2:-}"
      shift 2
      ;;
    --version)
      VERSION="${2:-}"
      shift 2
      ;;
    --output)
      OUTPUT_DIR="${2:-}"
      shift 2
      ;;
    --native-publish)
      NATIVE_PUBLISH_OVERRIDE="${2:-}"
      shift 2
      ;;
    --wpf-publish)
      WPF_PUBLISH_OVERRIDE="${2:-}"
      shift 2
      ;;
    --package-file-name)
      PACKAGE_FILE_NAME="${2:-}"
      shift 2
      ;;
    --skip-publish)
      SKIP_PUBLISH=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUTPUT_DIR="${OUTPUT_DIR:-$REPO_ROOT/artifacts/linux-installer}"
STAGE_ROOT="$OUTPUT_DIR/stage"
PACKAGE_NAME="jackllm-workstation"
ARCH="amd64"
SAFE_VERSION="$(printf '%s' "$VERSION" | tr ':\\/ ' '____')"
PACKAGE_ROOT="$STAGE_ROOT/${PACKAGE_NAME}_${SAFE_VERSION}_${ARCH}"
NATIVE_PUBLISH="$REPO_ROOT/JackLLM.Workstation/bin/$CONFIGURATION/net8.0/linux-x64/publish"
WPF_PUBLISH="$REPO_ROOT/JackLLM/bin/$CONFIGURATION/net8.0-windows7.0/win-x64/publish"
if [ -n "$NATIVE_PUBLISH_OVERRIDE" ]; then
  NATIVE_PUBLISH="$NATIVE_PUBLISH_OVERRIDE"
fi
if [ -n "$WPF_PUBLISH_OVERRIDE" ]; then
  WPF_PUBLISH="$WPF_PUBLISH_OVERRIDE"
fi
PACKAGE_FILE_NAME="${PACKAGE_FILE_NAME:-${PACKAGE_NAME}_${SAFE_VERSION}_${ARCH}.deb}"
DEB_PATH="$OUTPUT_DIR/$PACKAGE_FILE_NAME"

require_tool() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required tool not found: $1" >&2
    exit 1
  fi
}

require_tool dpkg-deb

if [ "$SKIP_PUBLISH" != "1" ]; then
  require_tool dotnet

  dotnet publish "$REPO_ROOT/JackLLM.Workstation/JackLLM.Workstation.csproj" \
    --configuration "$CONFIGURATION" \
    --runtime linux-x64 \
    --self-contained true \
    --nologo \
    -v:minimal

  dotnet publish "$REPO_ROOT/JackLLM/JackLLM.csproj" \
    --configuration "$CONFIGURATION" \
    --runtime win-x64 \
    --self-contained true \
    --nologo \
    -v:minimal
fi

if [ ! -f "$NATIVE_PUBLISH/JackLLM.Workstation" ]; then
  echo "Native publish output missing: $NATIVE_PUBLISH/JackLLM.Workstation" >&2
  exit 1
fi

if [ ! -f "$WPF_PUBLISH/JackLLM.exe" ]; then
  echo "WPF publish output missing: $WPF_PUBLISH/JackLLM.exe" >&2
  exit 1
fi

rm -rf "$PACKAGE_ROOT"
mkdir -p \
  "$PACKAGE_ROOT/DEBIAN" \
  "$PACKAGE_ROOT/opt/jackllm/workstation/native" \
  "$PACKAGE_ROOT/opt/jackllm/workstation/wpf" \
  "$PACKAGE_ROOT/usr/bin" \
  "$PACKAGE_ROOT/usr/share/doc/$PACKAGE_NAME" \
  "$PACKAGE_ROOT/usr/share/applications" \
  "$PACKAGE_ROOT/usr/lib/systemd/user" \
  "$PACKAGE_ROOT/var/lib/jackllm/Models" \
  "$PACKAGE_ROOT/var/lib/jackllm/CompleteModels" \
  "$PACKAGE_ROOT/var/lib/jackllm/Tools" \
  "$PACKAGE_ROOT/var/lib/jackllm/Agents" \
  "$PACKAGE_ROOT/var/lib/jackllm/Python" \
  "$PACKAGE_ROOT/var/log/jackllm"

cp -a "$NATIVE_PUBLISH/." "$PACKAGE_ROOT/opt/jackllm/workstation/native/"
cp -a "$WPF_PUBLISH/." "$PACKAGE_ROOT/opt/jackllm/workstation/wpf/"
mkdir -p "$PACKAGE_ROOT/opt/jackllm/workstation/native/install/linux"
cp -f "$REPO_ROOT/tools/linux/install-jackllm-cuda-pytorch.sh" "$PACKAGE_ROOT/opt/jackllm/workstation/native/install/linux/install-jackllm-cuda-pytorch.sh"
chmod +x "$PACKAGE_ROOT/opt/jackllm/workstation/native/JackLLM.Workstation" \
  "$PACKAGE_ROOT/opt/jackllm/workstation/native/install/linux/install-jackllm-cuda-pytorch.sh"

cat > "$PACKAGE_ROOT/usr/bin/jackllm-workstation" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
APP_ROOT="/opt/jackllm/workstation"
WPF_ROOT="$APP_ROOT/wpf"
load_models_location_hint() {
  for hint in "${JACKLLM_MODELS_LOCATION_ENV_PATH:-}" "$HOME/.config/jackllm/jackllm-models-location.env" "/var/lib/jackllm/jackllm-models-location.env" "$WPF_ROOT/jackllm-models-location.env"; do
    [ -n "$hint" ] || continue
    [ -f "$hint" ] || continue
    set -a
    # shellcheck disable=SC1090
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
export JACKONNX_AUTO_CUDA_TORCH="${JACKONNX_AUTO_CUDA_TORCH:-1}"
export HF_HOME="${HF_HOME:-$JACKLLM_MODELS_LOCATION/HuggingFace}"
export HUGGINGFACE_HUB_CACHE="${HUGGINGFACE_HUB_CACHE:-$HF_HOME/hub}"
export TRANSFORMERS_CACHE="${TRANSFORMERS_CACHE:-$HF_HOME/transformers}"
export HF_XET_CACHE="${HF_XET_CACHE:-$HF_HOME/xet}"
export JACKLLM_RESTORE_LOADED_MODELS_ON_STARTUP="${JACKLLM_RESTORE_LOADED_MODELS_ON_STARTUP:-0}"
export NVIDIA_SMI_PATH="${NVIDIA_SMI_PATH:-/usr/bin/nvidia-smi}"
export JACKLLM_NVIDIA_SMI="${JACKLLM_NVIDIA_SMI:-/usr/bin/nvidia-smi}"
mkdir -p "$JACKLLM_MODELS_LOCATION" "$JACKLLM_MODEL_ROOT" "$JACKLLM_COMPLETE_MODEL_ROOT" "$JACKLLM_TOOL_ROOT" "$JACKLLM_AGENT_ROOT" "$(dirname "$JACKONNX_PYTHON_HOME")" "$HF_HOME" "$HUGGINGFACE_HUB_CACHE" "$TRANSFORMERS_CACHE" "$HF_XET_CACHE" "$HOME/.local/state/jackllm"
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
SH

cat > "$PACKAGE_ROOT/usr/bin/jackllm-workstation-native" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
BASE="/opt/jackllm/workstation/native"
WPF_ROOT="/opt/jackllm/workstation/wpf"
load_models_location_hint() {
  for hint in "${JACKLLM_MODELS_LOCATION_ENV_PATH:-}" "$HOME/.config/jackllm/jackllm-models-location.env" "/var/lib/jackllm/jackllm-models-location.env" "$WPF_ROOT/jackllm-models-location.env"; do
    [ -n "$hint" ] || continue
    [ -f "$hint" ] || continue
    set -a
    # shellcheck disable=SC1090
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
HF_HOME="${HF_HOME:-$MODELS_LOCATION/HuggingFace}"
HUGGINGFACE_HUB_CACHE="${HUGGINGFACE_HUB_CACHE:-$HF_HOME/hub}"
TRANSFORMERS_CACHE="${TRANSFORMERS_CACHE:-$HF_HOME/transformers}"
HF_XET_CACHE="${HF_XET_CACHE:-$HF_HOME/xet}"
mkdir -p "$DATA" "$MODEL_ROOT" "$COMPLETE_MODEL_ROOT" "$TOOL_ROOT" "$AGENT_ROOT" "$(dirname "$PYTHON_HOME")" "$(dirname "$LOG")" "$HF_HOME" "$HUGGINGFACE_HUB_CACHE" "$TRANSFORMERS_CACHE" "$HF_XET_CACHE"
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
export JACKONNX_AUTO_CUDA_TORCH="${JACKONNX_AUTO_CUDA_TORCH:-1}"
export HF_HOME
export HUGGINGFACE_HUB_CACHE
export TRANSFORMERS_CACHE
export HF_XET_CACHE
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
SH

cat > "$PACKAGE_ROOT/usr/bin/jackllm-workstation-stop" <<'SH'
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
SH

cat > "$PACKAGE_ROOT/usr/bin/jackllm-workstation-start-shortcut" <<'SH'
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
SH

cat > "$PACKAGE_ROOT/usr/bin/jackllm-workstation-stop-shortcut" <<'SH'
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
SH

cat > "$PACKAGE_ROOT/usr/bin/jackllm-workstation-info" <<'SH'
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
SH

ln -sf jackllm-workstation "$PACKAGE_ROOT/usr/bin/llmworkstation"
ln -sf jackllm-workstation "$PACKAGE_ROOT/usr/bin/llm-workstation"
ln -sf jackllm-workstation-info "$PACKAGE_ROOT/usr/bin/llmworkstation-info"

chmod +x \
  "$PACKAGE_ROOT/usr/bin/jackllm-workstation" \
  "$PACKAGE_ROOT/usr/bin/jackllm-workstation-native" \
  "$PACKAGE_ROOT/usr/bin/jackllm-workstation-stop" \
  "$PACKAGE_ROOT/usr/bin/jackllm-workstation-start-shortcut" \
  "$PACKAGE_ROOT/usr/bin/jackllm-workstation-stop-shortcut" \
  "$PACKAGE_ROOT/usr/bin/jackllm-workstation-info"

cat > "$PACKAGE_ROOT/usr/share/doc/$PACKAGE_NAME/README.txt" <<'TXT'
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
TXT

cat > "$PACKAGE_ROOT/usr/share/applications/jackllm-workstation.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=JackLLM Workstation
Comment=Start the Linux native backend and open the JackLLM Workstation WPF UI through Wine
Exec=/usr/bin/jackllm-workstation-start-shortcut
Icon=wine
Terminal=false
Categories=Development;Utility;
StartupNotify=true
DESKTOP

cat > "$PACKAGE_ROOT/usr/share/applications/jackllm-workstation-stop.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=STOP JACKLLM WORKSTATION
Comment=Stop JackLLM Workstation Wine and the Linux native backend bridge
Exec=/usr/bin/jackllm-workstation-stop-shortcut
Icon=process-stop
Terminal=false
Categories=Development;Utility;
StartupNotify=false
DESKTOP

cat > "$PACKAGE_ROOT/usr/share/applications/jackllm-workstation-info.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=JackLLM Workstation Info
Comment=Show JackLLM Workstation Linux install paths and commands
Exec=/bin/sh -lc '/usr/bin/jackllm-workstation-info; printf "\nPress Enter to close..."; read _'
Icon=help-about
Terminal=true
Categories=Development;Utility;
StartupNotify=false
DESKTOP

cat > "$PACKAGE_ROOT/usr/lib/systemd/user/jackllm-workstation-native.service" <<'UNIT'
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
UNIT

installed_size="$(du -sk "$PACKAGE_ROOT" | awk '{print $1}')"
cat > "$PACKAGE_ROOT/DEBIAN/control" <<EOF
Package: $PACKAGE_NAME
Version: $VERSION
Section: utils
Priority: optional
Architecture: $ARCH
Installed-Size: $installed_size
Maintainer: SocketJack <support@socketjack.com>
Depends: bash, ca-certificates, curl, python3, python3-venv, python3-pip, wine64 | wine, libgomp1, libstdc++6
Recommends: xdg-utils, desktop-file-utils, nvidia-utils-550 | nvidia-utils | nvidia-driver, libcudart12, libcublas12, libcublaslt12, libcufft11, libcudnn9-cuda-12
Description: JackLLM Workstation for Linux
 Wine-hosted JackLLM WPF Workstation with a Linux-native LlmRuntime backend.
EOF

cat > "$PACKAGE_ROOT/DEBIAN/postinst" <<'SH'
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
SH

cat > "$PACKAGE_ROOT/DEBIAN/prerm" <<'SH'
#!/usr/bin/env bash
set -e
if command -v jackllm-workstation-stop >/dev/null 2>&1; then
  jackllm-workstation-stop >/dev/null 2>&1 || true
fi
exit 0
SH

cat > "$PACKAGE_ROOT/DEBIAN/postrm" <<'SH'
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
SH

chmod 0755 "$PACKAGE_ROOT/DEBIAN/postinst" "$PACKAGE_ROOT/DEBIAN/prerm" "$PACKAGE_ROOT/DEBIAN/postrm"

find "$PACKAGE_ROOT/opt/jackllm/workstation/native" -type f -name '*.sh' -exec chmod +x {} \;
rm -f "$DEB_PATH"
dpkg-deb --build --root-owner-group "$PACKAGE_ROOT" "$DEB_PATH"
echo "Built $DEB_PATH"
