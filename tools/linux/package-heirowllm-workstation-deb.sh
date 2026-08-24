#!/usr/bin/env bash
set -Eeuo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
VERSION="${HEIROWLLM_WORKSTATION_VERSION:-1:26.0.1}"
OUTPUT_DIR=""
SKIP_PUBLISH=0
NATIVE_PUBLISH_OVERRIDE=""
WPF_PUBLISH_OVERRIDE=""
PACKAGE_FILE_NAME=""

usage() {
  cat <<'EOF'
Usage: package-heirowllm-workstation-deb.sh [options]

Builds a Debian package for heirowLLM Workstation Linux.

Options:
  --configuration VALUE   Build configuration. Default: Release.
  --version VALUE         Debian package version. Default: 1:26.0.1.
  --output PATH           Output directory. Default: artifacts/linux-installer.
  --native-publish PATH   Existing linux-x64 heirowLLM.Workstation publish folder.
  --wpf-publish PATH      Existing win-x64 heirowLLM WPF publish folder.
  --package-file-name     Output .deb file name. Default: heirowllm-workstation_<version>_amd64.deb.
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
PACKAGE_NAME="heirowllm-workstation"
ARCH="amd64"
SAFE_VERSION="$(printf '%s' "$VERSION" | tr ':\\/ ' '____')"
PACKAGE_ROOT="$STAGE_ROOT/${PACKAGE_NAME}_${SAFE_VERSION}_${ARCH}"
NATIVE_PUBLISH="$REPO_ROOT/heirowLLM.Workstation/bin/$CONFIGURATION/net8.0/linux-x64/publish"
WPF_PUBLISH="$REPO_ROOT/heirowLLM/bin/$CONFIGURATION/net8.0-windows7.0/win-x64/publish"
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

  dotnet publish "$REPO_ROOT/heirowLLM.Workstation/heirowLLM.Workstation.csproj" \
    --configuration "$CONFIGURATION" \
    --runtime linux-x64 \
    --self-contained true \
    --nologo \
    -v:minimal

  dotnet publish "$REPO_ROOT/heirowLLM/heirowLLM.csproj" \
    --configuration "$CONFIGURATION" \
    --runtime win-x64 \
    --self-contained true \
    --nologo \
    -v:minimal
fi

if [ ! -f "$NATIVE_PUBLISH/heirowLLM.Workstation" ]; then
  echo "Native publish output missing: $NATIVE_PUBLISH/heirowLLM.Workstation" >&2
  exit 1
fi

if [ ! -f "$WPF_PUBLISH/heirowLLM.exe" ]; then
  echo "WPF publish output missing: $WPF_PUBLISH/heirowLLM.exe" >&2
  exit 1
fi

rm -rf "$PACKAGE_ROOT"
mkdir -p \
  "$PACKAGE_ROOT/DEBIAN" \
  "$PACKAGE_ROOT/opt/heirowllm/workstation/native" \
  "$PACKAGE_ROOT/opt/heirowllm/workstation/wpf" \
  "$PACKAGE_ROOT/usr/bin" \
  "$PACKAGE_ROOT/usr/share/doc/$PACKAGE_NAME" \
  "$PACKAGE_ROOT/usr/share/applications" \
  "$PACKAGE_ROOT/usr/lib/systemd/user" \
  "$PACKAGE_ROOT/var/lib/heirowllm/Models" \
  "$PACKAGE_ROOT/var/lib/heirowllm/CompleteModels" \
  "$PACKAGE_ROOT/var/lib/heirowllm/Tools" \
  "$PACKAGE_ROOT/var/lib/heirowllm/Agents" \
  "$PACKAGE_ROOT/var/lib/heirowllm/Python" \
  "$PACKAGE_ROOT/var/log/heirowllm"

cp -a "$NATIVE_PUBLISH/." "$PACKAGE_ROOT/opt/heirowllm/workstation/native/"
cp -a "$WPF_PUBLISH/." "$PACKAGE_ROOT/opt/heirowllm/workstation/wpf/"
mkdir -p "$PACKAGE_ROOT/opt/heirowllm/workstation/native/install/linux"
cp -f "$REPO_ROOT/tools/linux/install-heirowllm-cuda-pytorch.sh" "$PACKAGE_ROOT/opt/heirowllm/workstation/native/install/linux/install-heirowllm-cuda-pytorch.sh"
chmod +x "$PACKAGE_ROOT/opt/heirowllm/workstation/native/heirowLLM.Workstation" \
  "$PACKAGE_ROOT/opt/heirowllm/workstation/native/install/linux/install-heirowllm-cuda-pytorch.sh"

cat > "$PACKAGE_ROOT/usr/bin/heirowllm-workstation" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
APP_ROOT="/opt/heirowllm/workstation"
WPF_ROOT="$APP_ROOT/wpf"
load_models_location_hint() {
  for hint in "${HEIROWLLM_MODELS_LOCATION_ENV_PATH:-}" "$HOME/.config/heirowllm/heirowllm-models-location.env" "/var/lib/heirowllm/heirowllm-models-location.env" "$WPF_ROOT/heirowllm-models-location.env"; do
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
export WINEPREFIX="${WINEPREFIX:-$HOME/.wine-heirowllm-wpf}"
export WINEARCH="${WINEARCH:-win64}"
export WINEDEBUG="${WINEDEBUG:-fixme-all}"
export HEIROWLLM_WINE_SAFE_WPF="${HEIROWLLM_WINE_SAFE_WPF:-1}"
export HEIROWLLM_ENABLE_LINUX_NATIVE_BACKEND="${HEIROWLLM_ENABLE_LINUX_NATIVE_BACKEND:-1}"
export HEIROWLLM_NATIVE_BACKEND_PATH="${HEIROWLLM_NATIVE_BACKEND_PATH:-$APP_ROOT/native/heirowLLM.Workstation}"
export HEIROWLLM_NATIVE_BACKEND_LOG="${HEIROWLLM_NATIVE_BACKEND_LOG:-/var/log/heirowllm/native-backend.log}"
export HEIROWLLM_MODELS_LOCATION="${HEIROWLLM_MODELS_LOCATION:-/var/lib/heirowllm}"
export HEIROWLLM_DATA_ROOT="${HEIROWLLM_DATA_ROOT:-/var/lib/heirowllm}"
export HEIROWLLM_MODEL_ROOT="${HEIROWLLM_MODEL_ROOT:-$HEIROWLLM_MODELS_LOCATION/Models}"
export HEIROWLLM_COMPLETE_MODEL_ROOT="${HEIROWLLM_COMPLETE_MODEL_ROOT:-$HEIROWLLM_MODELS_LOCATION/CompleteModels}"
export HEIROWLLM_TOOL_ROOT="${HEIROWLLM_TOOL_ROOT:-$HEIROWLLM_DATA_ROOT/Tools}"
export HEIROWLLM_AGENT_ROOT="${HEIROWLLM_AGENT_ROOT:-$HEIROWLLM_DATA_ROOT/Agents}"
export JACKONNX_PYTHON_HOME="${JACKONNX_PYTHON_HOME:-$HEIROWLLM_DATA_ROOT/Python}"
export JACKONNX_PYTHON="${JACKONNX_PYTHON:-$JACKONNX_PYTHON_HOME/bin/python3}"
export HEIROWLLM_MODELS_MANAGER_RUNTIME_URL="${HEIROWLLM_MODELS_MANAGER_RUNTIME_URL:-http://127.0.0.1:12435}"
export HEIROWLLM_LLMRUNTIME_URL="${HEIROWLLM_LLMRUNTIME_URL:-http://127.0.0.1:12435}"
export HEIROWLLM_EXTERNAL_BROWSER="${HEIROWLLM_EXTERNAL_BROWSER:-1}"
export JACKONNX_AUTO_CUDA_TORCH="${JACKONNX_AUTO_CUDA_TORCH:-1}"
export HF_HOME="${HF_HOME:-$HEIROWLLM_MODELS_LOCATION/HuggingFace}"
export HUGGINGFACE_HUB_CACHE="${HUGGINGFACE_HUB_CACHE:-$HF_HOME/hub}"
export TRANSFORMERS_CACHE="${TRANSFORMERS_CACHE:-$HF_HOME/transformers}"
export HF_XET_CACHE="${HF_XET_CACHE:-$HF_HOME/xet}"
export HEIROWLLM_RESTORE_LOADED_MODELS_ON_STARTUP="${HEIROWLLM_RESTORE_LOADED_MODELS_ON_STARTUP:-0}"
export NVIDIA_SMI_PATH="${NVIDIA_SMI_PATH:-/usr/bin/nvidia-smi}"
export HEIROWLLM_NVIDIA_SMI="${HEIROWLLM_NVIDIA_SMI:-/usr/bin/nvidia-smi}"
if [ -z "${HEIROWLLM_VLLM_PYTHON:-}" ] && [ -x /opt/vllm/venv/bin/python ]; then
  export HEIROWLLM_VLLM_PYTHON=/opt/vllm/venv/bin/python
fi
export HEIROWLLM_VLLM_ARGS="${HEIROWLLM_VLLM_ARGS:---dtype auto --enforce-eager}"
mkdir -p "$HEIROWLLM_MODELS_LOCATION" "$HEIROWLLM_MODEL_ROOT" "$HEIROWLLM_COMPLETE_MODEL_ROOT" "$HEIROWLLM_TOOL_ROOT" "$HEIROWLLM_AGENT_ROOT" "$(dirname "$JACKONNX_PYTHON_HOME")" "$HF_HOME" "$HUGGINGFACE_HUB_CACHE" "$TRANSFORMERS_CACHE" "$HF_XET_CACHE" "$HOME/.local/state/heirowllm"
WINE_BIN="${WINE_BIN:-}"
if [ -z "$WINE_BIN" ]; then
  WINE_BIN="$(command -v wine 2>/dev/null || command -v wine64 2>/dev/null || true)"
fi
if [ -z "$WINE_BIN" ]; then
  echo "wine or wine64 was not found. Install Wine before launching heirowLLM Workstation." >&2
  exit 1
fi
if [ "${HEIROWLLM_PRESTART_NATIVE_BACKEND:-1}" != "0" ] && [ "${HEIROWLLM_DISABLE_LINUX_NATIVE_BACKEND:-0}" != "1" ]; then
  if ! curl -fsS --max-time 2 "$HEIROWLLM_LLMRUNTIME_URL/api/v1/runtime/compatibility" >/dev/null 2>&1; then
    mkdir -p "$(dirname "$HEIROWLLM_NATIVE_BACKEND_LOG")"
    nohup env HEIROWLLM_CHAT_PORT="${HEIROWLLM_NATIVE_CHAT_PORT:-12437}" heirowllm-workstation-native >> "$HEIROWLLM_NATIVE_BACKEND_LOG" 2>&1 < /dev/null &
    for _ in $(seq 1 45); do
      if curl -fsS --max-time 2 "$HEIROWLLM_LLMRUNTIME_URL/api/v1/runtime/compatibility" >/dev/null 2>&1; then
        break
      fi
      sleep 1
    done
  fi
fi
if [ -z "${DISPLAY:-}" ]; then
  if command -v xvfb-run >/dev/null 2>&1; then
    cd "$WPF_ROOT"
    exec xvfb-run -a -s "${HEIROWLLM_XVFB_SERVER_ARGS:--screen 0 1280x720x24 -nolisten tcp}" "$WINE_BIN" "$WPF_ROOT/heirowLLM.exe" "$@"
  elif command -v Xvfb >/dev/null 2>&1; then
    export DISPLAY="${HEIROWLLM_XVFB_DISPLAY:-:10}"
    if ! pgrep -u "$USER" -f "Xvfb $DISPLAY" >/dev/null 2>&1; then
      Xvfb "$DISPLAY" -screen 0 "${HEIROWLLM_XVFB_SCREEN:-1280x720x24}" -nolisten tcp >/tmp/heirowllm-xvfb.log 2>&1 &
      sleep 1
    fi
  else
    echo "No DISPLAY is set and Xvfb is not installed. Install xvfb or launch from a desktop session." >&2
    exit 1
  fi
fi
cd "$WPF_ROOT"
exec "$WINE_BIN" "$WPF_ROOT/heirowLLM.exe" "$@"
SH

cat > "$PACKAGE_ROOT/usr/bin/heirowllm-workstation-native" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
BASE="/opt/heirowllm/workstation/native"
WPF_ROOT="/opt/heirowllm/workstation/wpf"
load_models_location_hint() {
  for hint in "${HEIROWLLM_MODELS_LOCATION_ENV_PATH:-}" "$HOME/.config/heirowllm/heirowllm-models-location.env" "/var/lib/heirowllm/heirowllm-models-location.env" "$WPF_ROOT/heirowllm-models-location.env"; do
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
DATA="${HEIROWLLM_DATA_ROOT:-/var/lib/heirowllm}"
LOG="${HEIROWLLM_NATIVE_BACKEND_LOG:-/var/log/heirowllm/native-backend.log}"
MODELS_LOCATION="${HEIROWLLM_MODELS_LOCATION:-$DATA}"
MODEL_ROOT="${HEIROWLLM_MODEL_ROOT:-$MODELS_LOCATION/Models}"
COMPLETE_MODEL_ROOT="${HEIROWLLM_COMPLETE_MODEL_ROOT:-$MODELS_LOCATION/CompleteModels}"
TOOL_ROOT="${HEIROWLLM_TOOL_ROOT:-$DATA/Tools}"
AGENT_ROOT="${HEIROWLLM_AGENT_ROOT:-$DATA/Agents}"
PYTHON_HOME="${JACKONNX_PYTHON_HOME:-$DATA/Python}"
PYTHON_EXE="${JACKONNX_PYTHON:-$PYTHON_HOME/bin/python3}"
CHAT_PORT="${HEIROWLLM_CHAT_PORT:-11436}"
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
export HEIROWLLM_CONTENT_ROOT="$BASE"
export HEIROWLLM_DATA_ROOT="$DATA"
export HEIROWLLM_MODELS_LOCATION="$MODELS_LOCATION"
export HEIROWLLM_MODEL_ROOT="$MODEL_ROOT"
export HEIROWLLM_COMPLETE_MODEL_ROOT="$COMPLETE_MODEL_ROOT"
export HEIROWLLM_TOOL_ROOT="$TOOL_ROOT"
export HEIROWLLM_AGENT_ROOT="$AGENT_ROOT"
export JACKONNX_PYTHON_HOME="$PYTHON_HOME"
export JACKONNX_PYTHON="$PYTHON_EXE"
export JACKONNX_AUTO_CUDA_TORCH="${JACKONNX_AUTO_CUDA_TORCH:-1}"
export HF_HOME
export HUGGINGFACE_HUB_CACHE
export TRANSFORMERS_CACHE
export HF_XET_CACHE
RESTORE_LOADED_MODELS="${HEIROWLLM_RESTORE_LOADED_MODELS_ON_STARTUP:-1}"
RESTORE_ARGS=()
case "${RESTORE_LOADED_MODELS,,}" in
  0|false|no|off) RESTORE_ARGS+=(--no-restore-loaded-models) ;;
esac
export NVIDIA_SMI_PATH="${NVIDIA_SMI_PATH:-/usr/bin/nvidia-smi}"
export HEIROWLLM_NVIDIA_SMI="${HEIROWLLM_NVIDIA_SMI:-/usr/bin/nvidia-smi}"
if [ -z "${HEIROWLLM_VLLM_PYTHON:-}" ] && [ -x /opt/vllm/venv/bin/python ]; then
  export HEIROWLLM_VLLM_PYTHON=/opt/vllm/venv/bin/python
fi
export HEIROWLLM_VLLM_ARGS="${HEIROWLLM_VLLM_ARGS:---dtype auto --enforce-eager}"
export LD_LIBRARY_PATH="$BASE:$LLAMA_NATIVE_DIRS:$CUDA_PY_DIRS:${LD_LIBRARY_PATH:-}"
cd "$BASE"
exec "$BASE/heirowLLM.Workstation" \
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

cat > "$PACKAGE_ROOT/usr/bin/heirowllm-workstation-stop" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
if command -v wineserver >/dev/null 2>&1; then
  WINEPREFIX="${WINEPREFIX:-$HOME/.wine-heirowllm-wpf}" wineserver -k >/dev/null 2>&1 || true
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

cat > "$PACKAGE_ROOT/usr/bin/heirowllm-workstation-start-shortcut" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
export HEIROWLLM_PRESTART_NATIVE_BACKEND=1
export HEIROWLLM_ENABLE_LINUX_NATIVE_BACKEND=1
export HEIROWLLM_RESTORE_LOADED_MODELS_ON_STARTUP="${HEIROWLLM_RESTORE_LOADED_MODELS_ON_STARTUP:-0}"
runtime_url="${HEIROWLLM_LLMRUNTIME_URL:-http://127.0.0.1:12435}"
native_log="${HEIROWLLM_NATIVE_BACKEND_LOG:-/var/log/heirowllm/native-backend.log}"
if ! curl -fsS --max-time 2 "$runtime_url/api/v1/runtime/compatibility" >/dev/null 2>&1; then
  mkdir -p "$(dirname "$native_log")"
  nohup env HEIROWLLM_CHAT_PORT="${HEIROWLLM_NATIVE_CHAT_PORT:-12437}" /usr/bin/heirowllm-workstation-native >> "$native_log" 2>&1 < /dev/null &
  for _ in $(seq 1 45); do
    if curl -fsS --max-time 2 "$runtime_url/api/v1/runtime/compatibility" >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done
fi
exec /usr/bin/heirowllm-workstation "$@"
SH

cat > "$PACKAGE_ROOT/usr/bin/heirowllm-workstation-stop-shortcut" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
/usr/bin/heirowllm-workstation-stop "$@" || true
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

cat > "$PACKAGE_ROOT/usr/bin/heirowllm-workstation-info" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
cat <<'INFO'
heirowLLM Workstation Linux

Run the WPF UI through Wine:
  heirowllm-workstation

Browser workstation URL:
  http://127.0.0.1:11436/

Stop the WPF UI and native backend:
  heirowllm-workstation-stop

Run only the hidden native backend:
  heirowllm-workstation-native

Install locations:
  WPF app:        /opt/heirowllm/workstation/wpf/heirowLLM.exe
  Native backend: /opt/heirowllm/workstation/native/heirowLLM.Workstation
  App root:       /opt/heirowllm/workstation

Mutable data:
  Models location env: /var/lib/heirowllm/heirowllm-models-location.env
  Models root:         /var/lib/heirowllm/Models
  CompleteModels root: /var/lib/heirowllm/CompleteModels
  Tools root:          /var/lib/heirowllm/Tools
  Agents root:         /var/lib/heirowllm/Agents
  Python root:         /var/lib/heirowllm/Python
  Logs:                /var/log/heirowllm
  Install status:      /var/lib/heirowllm/install-status.json

Desktop menu entries:
  heirowLLM Workstation
  STOP HEIROWLLM WORKSTATION
  heirowLLM Workstation Info

If a desktop menu does not refresh immediately, run:
  gtk-launch heirowllm-workstation
INFO
SH

ln -sf heirowllm-workstation "$PACKAGE_ROOT/usr/bin/llmworkstation"
ln -sf heirowllm-workstation "$PACKAGE_ROOT/usr/bin/llm-workstation"
ln -sf heirowllm-workstation-info "$PACKAGE_ROOT/usr/bin/llmworkstation-info"

chmod +x \
  "$PACKAGE_ROOT/usr/bin/heirowllm-workstation" \
  "$PACKAGE_ROOT/usr/bin/heirowllm-workstation-native" \
  "$PACKAGE_ROOT/usr/bin/heirowllm-workstation-stop" \
  "$PACKAGE_ROOT/usr/bin/heirowllm-workstation-start-shortcut" \
  "$PACKAGE_ROOT/usr/bin/heirowllm-workstation-stop-shortcut" \
  "$PACKAGE_ROOT/usr/bin/heirowllm-workstation-info"

cat > "$PACKAGE_ROOT/usr/share/doc/$PACKAGE_NAME/README.txt" <<'TXT'
heirowLLM Workstation Linux
=========================

Run:
  heirowllm-workstation

Browser workstation URL:
  http://127.0.0.1:11436/

Stop:
  heirowllm-workstation-stop

Show this install layout:
  heirowllm-workstation-info

Install locations:
  WPF app:        /opt/heirowllm/workstation/wpf/heirowLLM.exe
  Native backend: /opt/heirowllm/workstation/native/heirowLLM.Workstation
  App root:       /opt/heirowllm/workstation

Mutable data:
  Models root:         /var/lib/heirowllm/Models
  CompleteModels root: /var/lib/heirowllm/CompleteModels
  Tools root:          /var/lib/heirowllm/Tools
  Agents root:         /var/lib/heirowllm/Agents
  Python root:         /var/lib/heirowllm/Python
  Logs:                /var/log/heirowllm
  Install status:      /var/lib/heirowllm/install-status.json

The visible Linux GUI is the Wine-hosted WPF app. The launcher starts the
Linux-native backend for Wine sessions before opening WPF, and WPF monitors
the same runtime URL.
TXT

cat > "$PACKAGE_ROOT/usr/share/applications/heirowllm-workstation.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=heirowLLM Workstation
Comment=Start the Linux native backend and open the heirowLLM Workstation WPF UI through Wine
Exec=/usr/bin/heirowllm-workstation-start-shortcut
Icon=wine
Terminal=false
Categories=Development;Utility;
StartupNotify=true
DESKTOP

cat > "$PACKAGE_ROOT/usr/share/applications/heirowllm-workstation-stop.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=STOP HEIROWLLM WORKSTATION
Comment=Stop heirowLLM Workstation Wine and the Linux native backend bridge
Exec=/usr/bin/heirowllm-workstation-stop-shortcut
Icon=process-stop
Terminal=false
Categories=Development;Utility;
StartupNotify=false
DESKTOP

cat > "$PACKAGE_ROOT/usr/share/applications/heirowllm-workstation-info.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=heirowLLM Workstation Info
Comment=Show heirowLLM Workstation Linux install paths and commands
Exec=/bin/sh -lc '/usr/bin/heirowllm-workstation-info; printf "\nPress Enter to close..."; read _'
Icon=help-about
Terminal=true
Categories=Development;Utility;
StartupNotify=false
DESKTOP

cat > "$PACKAGE_ROOT/usr/lib/systemd/user/heirowllm-workstation-native.service" <<'UNIT'
[Unit]
Description=heirowLLM Workstation native backend
After=network-online.target

[Service]
Type=simple
ExecStart=/usr/bin/heirowllm-workstation-native
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
Depends: bash, ca-certificates, curl, python3, python3-venv, python3-pip, wine64 | wine, xvfb, libgomp1, libstdc++6
Recommends: xdg-utils, desktop-file-utils, nvidia-utils-550 | nvidia-utils | nvidia-driver, libcudart12, libcublas12, libcublaslt12, libcufft11, libcudnn9-cuda-12
Description: heirowLLM Workstation for Linux
 Wine-hosted heirowLLM WPF Workstation with a Linux-native LlmRuntime backend.
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
  for launcher in heirowllm-workstation heirowllm-workstation-stop heirowllm-workstation-info; do
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

mkdir -p /var/lib/heirowllm/Models /var/lib/heirowllm/CompleteModels /var/lib/heirowllm/Tools /var/lib/heirowllm/Agents /var/lib/heirowllm/Python /var/log/heirowllm
chmod 0777 /var/lib/heirowllm /var/lib/heirowllm/Models /var/lib/heirowllm/CompleteModels /var/lib/heirowllm/Tools /var/lib/heirowllm/Agents /var/lib/heirowllm/Python /var/log/heirowllm || true
if [ ! -f /var/lib/heirowllm/heirowllm-models-location.env ]; then
  cat > /var/lib/heirowllm/heirowllm-models-location.env <<'EOF'
# Generated by heirowLLM Workstation installer. The WPF app rewrites this when Models Location changes.
HEIROWLLM_MODELS_LOCATION='/var/lib/heirowllm'
HEIROWLLM_MODEL_ROOT='/var/lib/heirowllm/Models'
HEIROWLLM_COMPLETE_MODEL_ROOT='/var/lib/heirowllm/CompleteModels'
EOF
fi
chmod 0666 /var/lib/heirowllm/heirowllm-models-location.env || true
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
cat > /var/lib/heirowllm/install-status.json <<EOF
{
  "installedAtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "appRoot": "/opt/heirowllm/workstation",
  "wpfApp": "/opt/heirowllm/workstation/wpf/heirowLLM.exe",
  "nativeBackend": "/opt/heirowllm/workstation/native/heirowLLM.Workstation",
  "runCommand": "heirowllm-workstation",
  "stopCommand": "heirowllm-workstation-stop",
  "infoCommand": "heirowllm-workstation-info",
  "desktopShortcuts": [
    "heirowllm-workstation.desktop",
    "heirowllm-workstation-stop.desktop",
    "heirowllm-workstation-info.desktop"
  ],
  "browserUrl": "http://127.0.0.1:11436/",
  "runtimeUrl": "http://127.0.0.1:12435/",
  "modelsRoot": "/var/lib/heirowllm/Models",
  "completeModelsRoot": "/var/lib/heirowllm/CompleteModels",
  "toolsRoot": "/var/lib/heirowllm/Tools",
  "agentsRoot": "/var/lib/heirowllm/Agents",
  "pythonRoot": "/var/lib/heirowllm/Python",
  "pythonExecutable": "/var/lib/heirowllm/Python/bin/python3",
  "logRoot": "/var/log/heirowllm",
  "nvidiaSmi": "$nvidia_smi",
  "cudaRuntimeDetected": $cuda_runtime,
  "cudaInstallPolicy": "recommended-packages-during-apt-install-when-available",
  "pytorchInstall": "user-confirmed-in-workstation"
}
EOF
chmod 0666 /var/lib/heirowllm/install-status.json || true
cat <<'MSG'

heirowLLM Workstation Linux installed.

Run:
  heirowllm-workstation

Stop:
  heirowllm-workstation-stop

Show install paths:
  heirowllm-workstation-info

Installed to:
  /opt/heirowllm/workstation

Models:
  /var/lib/heirowllm/Models
  /var/lib/heirowllm/CompleteModels
  /var/lib/heirowllm/Tools
  /var/lib/heirowllm/Agents
  /var/lib/heirowllm/Python

Logs:
  /var/log/heirowllm

Desktop menu entries:
  heirowLLM Workstation
  STOP HEIROWLLM WORKSTATION
  heirowLLM Workstation Info

Desktop shortcuts:
  ~/Desktop/heirowllm-workstation.desktop
  ~/Desktop/heirowllm-workstation-stop.desktop
  ~/Desktop/heirowllm-workstation-info.desktop

MSG
exit 0
SH

cat > "$PACKAGE_ROOT/DEBIAN/prerm" <<'SH'
#!/usr/bin/env bash
set -e
if command -v heirowllm-workstation-stop >/dev/null 2>&1; then
  heirowllm-workstation-stop >/dev/null 2>&1 || true
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
    "$target_home/Desktop/heirowllm-workstation.desktop" \
    "$target_home/Desktop/heirowllm-workstation-stop.desktop" \
    "$target_home/Desktop/heirowllm-workstation-info.desktop"
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
  rm -rf /var/lib/heirowllm /var/log/heirowllm
fi
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
fi
exit 0
SH

chmod 0755 "$PACKAGE_ROOT/DEBIAN/postinst" "$PACKAGE_ROOT/DEBIAN/prerm" "$PACKAGE_ROOT/DEBIAN/postrm"

find "$PACKAGE_ROOT/opt/heirowllm/workstation/native" -type f -name '*.sh' -exec chmod +x {} \;
rm -f "$DEB_PATH"
dpkg-deb --build --root-owner-group "$PACKAGE_ROOT" "$DEB_PATH"
echo "Built $DEB_PATH"
