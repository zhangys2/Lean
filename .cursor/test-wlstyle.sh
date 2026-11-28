#!/usr/bin/env bash
# Run the WLStyle suite from an isolated Release copy.
# Regression discovery loads pandas, and data-folder is ../../../Data/
# relative to the working directory, so the copy must live at
# <scratch>/Tests/bin/Release with <scratch>/Data linked at the repo Data.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENV="${LEAN_PY_VENV:-${HOME}/.local/share/lean-py311}"
ENV_FILE="${HOME}/.config/lean-cursor-env.sh"
RUN="${LEAN_WLSTYLE_RUN:-${HOME}/tmp/lean-wlstyle-run}"

if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  . "$ENV_FILE"
fi

if [[ "$RUN" == /tmp || "$RUN" == /tmp/* ]]; then
  echo "scratch must be under \$HOME/tmp, not /tmp" >&2
  exit 1
fi
if [[ "$RUN" == "$ROOT" || "$RUN" == "$ROOT"/* ]]; then
  echo "scratch must be outside the repo: ${RUN}" >&2
  exit 1
fi

DOTNET="${DOTNET_ROOT:-${HOME}/.dotnet}/dotnet"
if [[ ! -x "$DOTNET" ]]; then
  echo "dotnet not found at ${DOTNET}; run bash .cursor/install.sh" >&2
  exit 1
fi
if [[ ! -x "${VENV}/bin/python" ]]; then
  echo "Python venv missing at ${VENV}; run bash .cursor/install.sh" >&2
  exit 1
fi
if [[ ! -f "${ROOT}/Tests/bin/Release/QuantConnect.Tests.dll" ]]; then
  echo "Release test assembly missing; run bash .cursor/install.sh" >&2
  exit 1
fi
if [[ ! -d "${ROOT}/Data" ]]; then
  echo "repo Data directory missing at ${ROOT}/Data" >&2
  exit 1
fi

real_py="$(readlink -f "${VENV}/bin/python")"
prefix="$(cd "$(dirname "$real_py")/.." && pwd)"
pydll=""
for cand in "${prefix}/lib/libpython3.11.so" "${prefix}/lib/libpython3.11.so.1.0"; do
  if [[ -e "$cand" ]]; then
    pydll="$cand"
    break
  fi
done
if [[ -z "$pydll" ]]; then
  echo "libpython3.11 not found under ${prefix}/lib" >&2
  exit 1
fi
site="$("${VENV}/bin/python" -c 'import site; print(site.getsitepackages()[0])')"

export DOTNET_ROOT="$(dirname "$DOTNET")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export PYTHONNET_PYDLL="$pydll"
export PYTHONPATH="$site"

rm -rf "$RUN"
mkdir -p "${RUN}/Tests/bin" "$(dirname "$RUN")"
cp -a "${ROOT}/Tests/bin/Release" "${RUN}/Tests/bin/Release"
ln -s "${ROOT}/Data" "${RUN}/Data"

cd "${RUN}/Tests/bin/Release"
FILTER="${LEAN_WLSTYLE_FILTER:-FullyQualifiedName~WLStyle}"
if command -v ionice >/dev/null 2>&1; then
  nice -n 19 ionice -c3 "$DOTNET" test QuantConnect.Tests.dll --filter "$FILTER"
else
  nice -n 19 "$DOTNET" test QuantConnect.Tests.dll --filter "$FILTER"
fi
