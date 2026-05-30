#!/usr/bin/env bash
set -Eeuo pipefail

APP_ROOT=""
PYTHON_EXE=""
TORCH_INDEX_URL="${JACKONNX_TORCH_CUDA_INDEX_URL:-https://download.pytorch.org/whl/cu124}"
TORCH_VERSION="${JACKONNX_TORCH_VERSION:-}"
INSTALL_OS_DEPS="${JACKONNX_INSTALL_LINUX_CUDA_DEPS:-1}"
INSTALL_IMAGE_PACKAGES="${JACKONNX_INSTALL_IMAGE_PACKAGES:-1}"
REQUIRE_TORCH_CUDA="${JACKONNX_REQUIRE_TORCH_CUDA:-1}"

usage() {
  cat <<'EOF'
Usage: install-jackllm-cuda-pytorch.sh [options]

Installs the Linux Python/PyTorch/CUDA runtime used by JackLLM Workstation.

Options:
  --app-root PATH         JackLLM Workstation output root. Defaults to two levels above this script.
  --python PATH           Python executable or venv python to repair.
  --torch-index-url URL   PyTorch CUDA wheel index. Defaults to JACKONNX_TORCH_CUDA_INDEX_URL or cu124.
  --torch-version VALUE   Optional torch major/minor version pin, for example 2.8.
  --skip-os-deps          Do not try apt-get CUDA/Python package installs.
  --skip-image-packages   Do not install diffusers/transformers image packages.
  --allow-cpu-torch       Do not fail if torch.cuda.is_available() is false after install.
  -h, --help              Show this help.
EOF
}

log() {
  printf '[jackllm-linux-install] %s\n' "$*" >&2
}

fail() {
  log "ERROR: $*"
  exit 1
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --app-root)
      APP_ROOT="${2:-}"
      shift 2
      ;;
    --python)
      PYTHON_EXE="${2:-}"
      shift 2
      ;;
    --torch-index-url)
      TORCH_INDEX_URL="${2:-}"
      shift 2
      ;;
    --torch-version)
      TORCH_VERSION="${2:-}"
      shift 2
      ;;
    --skip-os-deps)
      INSTALL_OS_DEPS=0
      shift
      ;;
    --skip-image-packages)
      INSTALL_IMAGE_PACKAGES=0
      shift
      ;;
    --allow-cpu-torch)
      REQUIRE_TORCH_CUDA=0
      shift
      ;;
    --noninteractive)
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      fail "Unknown option: $1"
      ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -z "$APP_ROOT" ]; then
  if [ -n "${JACKONNX_PYTHON_HOME:-}" ]; then
    APP_ROOT="$(dirname "$JACKONNX_PYTHON_HOME")"
    mkdir -p "$APP_ROOT"
    APP_ROOT="$(cd "$APP_ROOT" && pwd)"
  else
    APP_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
    if [ "${APP_ROOT#/opt/}" != "$APP_ROOT" ] && [ ! -w "$APP_ROOT" ]; then
      APP_ROOT="${JACKLLM_DATA_ROOT:-/var/lib/jackllm}"
    fi
  fi
fi

if [ -z "$TORCH_INDEX_URL" ]; then
  TORCH_INDEX_URL="https://download.pytorch.org/whl/cu124"
fi

run_root() {
  if [ "$(id -u)" -eq 0 ]; then
    "$@"
    return
  fi

  if command -v sudo >/dev/null 2>&1; then
    sudo -n "$@"
    return
  fi

  return 91
}

apt_has_candidate() {
  local package="$1"
  apt-cache policy "$package" 2>/dev/null | awk '/Candidate:/ { print $2 }' | grep -qv '^(none)$'
}

install_apt_packages() {
  if ! command -v apt-get >/dev/null 2>&1; then
    log "apt-get not found; skipping OS CUDA/Python package install."
    return
  fi

  export DEBIAN_FRONTEND=noninteractive
  log "Refreshing apt package metadata."
  if ! run_root apt-get update; then
    log "Could not run apt-get update without passwordless sudo/root; continuing with Python-side install."
    return
  fi

  local base_packages=(ca-certificates curl python3 python3-venv python3-pip libgomp1 libstdc++6)
  log "Installing base Linux runtime packages: ${base_packages[*]}"
  run_root apt-get install -y "${base_packages[@]}" || log "Base package install failed; continuing with available packages."

  local optional_packages=(
    nvidia-utils-550
    libcudart12
    libcublas12
    libcublaslt12
    libcufft11
    libcudnn9-cuda-12
  )

  for package in "${optional_packages[@]}"; do
    if apt_has_candidate "$package"; then
      log "Installing optional CUDA runtime package: $package"
      run_root apt-get install -y "$package" || log "Optional package $package failed; continuing."
    else
      log "Optional CUDA runtime package not available from configured apt sources: $package"
    fi
  done
}

resolve_python() {
  if [ -n "$PYTHON_EXE" ]; then
    if [ -x "$PYTHON_EXE" ]; then
      printf '%s\n' "$PYTHON_EXE"
      return
    fi
    fail "Configured Python is not executable: $PYTHON_EXE"
  fi

  local bundled="$APP_ROOT/Python/bin/python3"
  if [ -x "$bundled" ]; then
    printf '%s\n' "$bundled"
    return
  fi

  local bundled_python="$APP_ROOT/Python/bin/python"
  if [ -x "$bundled_python" ]; then
    printf '%s\n' "$bundled_python"
    return
  fi

  if ! command -v python3 >/dev/null 2>&1; then
    fail "python3 was not found. Install python3/python3-venv or pass --python."
  fi

  mkdir -p "$APP_ROOT"
  log "Creating JackLLM Linux Python venv at $APP_ROOT/Python"
  python3 -m venv "$APP_ROOT/Python"

  if [ -x "$bundled" ]; then
    printf '%s\n' "$bundled"
    return
  fi
  if [ -x "$bundled_python" ]; then
    printf '%s\n' "$bundled_python"
    return
  fi

  fail "Python venv creation completed, but no venv python was found under $APP_ROOT/Python/bin."
}

if [ "$INSTALL_OS_DEPS" != "0" ]; then
  install_apt_packages
fi

if command -v nvidia-smi >/dev/null 2>&1; then
  log "Detected NVIDIA driver:"
  nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader || true
else
  log "nvidia-smi was not found. Install an NVIDIA driver before expecting torch.cuda.is_available()."
fi

PYTHON_EXE="$(resolve_python)"
log "Using Python: $PYTHON_EXE"

log "Upgrading pip tooling."
"$PYTHON_EXE" -m pip install --disable-pip-version-check --upgrade pip setuptools wheel

torch_packages=(torch torchvision torchaudio)
if [ -n "$TORCH_VERSION" ]; then
  torch_packages=("torch==${TORCH_VERSION}.*" torchvision torchaudio)
fi

log "Installing CUDA-enabled PyTorch from $TORCH_INDEX_URL"
"$PYTHON_EXE" -m pip install \
  --disable-pip-version-check \
  --no-input \
  --upgrade \
  --force-reinstall \
  "${torch_packages[@]}" \
  --index-url "$TORCH_INDEX_URL"

if [ "$INSTALL_IMAGE_PACKAGES" != "0" ]; then
  log "Installing JackONNX PyTorch image/runtime helper packages."
  "$PYTHON_EXE" -m pip install \
    --disable-pip-version-check \
    --no-input \
    --upgrade \
    --no-cache-dir \
    "diffusers>=0.38.0" "transformers>=5.0.0" accelerate safetensors "huggingface-hub>=1.5.0,<2.0" sentencepiece protobuf pillow

  log "Checking Python image/runtime package dependency consistency."
  "$PYTHON_EXE" -m pip check
fi

log "Verifying PyTorch CUDA."
set +e
"$PYTHON_EXE" - <<'PY'
import json
import sys

try:
    import torch
    payload = {
        "torch": getattr(torch, "__version__", ""),
        "torch_cuda": getattr(torch.version, "cuda", "") or "",
        "cuda_available": bool(torch.cuda.is_available()),
        "device": torch.cuda.get_device_name(0) if torch.cuda.is_available() else "",
        "arch_list": list(torch.cuda.get_arch_list() or []) if hasattr(torch.cuda, "get_arch_list") else [],
    }
    print(json.dumps(payload, indent=2))
    sys.exit(0 if payload["cuda_available"] else 3)
except Exception as exc:
    print(json.dumps({"error": str(exc)}, indent=2))
    sys.exit(2)
PY
verify_exit=$?
set -e
if [ "$verify_exit" -ne 0 ] && [ "$REQUIRE_TORCH_CUDA" != "0" ]; then
  fail "PyTorch installed, but CUDA verification failed with exit code $verify_exit."
fi

if [ "$verify_exit" -ne 0 ]; then
  log "PyTorch CUDA verification failed, but --allow-cpu-torch/JACKONNX_REQUIRE_TORCH_CUDA=0 allowed completion."
fi

log "Linux CUDA/PyTorch install completed."
