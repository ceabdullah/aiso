#!/usr/bin/env bash
# One-time setup to run AISO locally on a Mac (Apple Silicon or Intel).
#
# Installs (via Homebrew, only what is missing):
#   - .NET 10 SDK            - Docker Desktop (runs the SQL Server container)
#   - Git                    - Visual Studio Code + C# Dev Kit (Visual Studio for Mac is retired)
# Homebrew no longer installs on Intel Macs. On an Intel Mac without it, the tools come from the vendors
# instead: .NET via Microsoft's dotnet-install.sh (into ~/.dotnet), Docker Desktop and VS Code as signed
# downloads that are checked for the vendor's Apple team ID and notarization before they are installed.
# Then configures the project: trusts the HTTPS dev certificate, restores packages, stores a generated
# SQL Server password in .NET user secrets, and creates the local workspace (~/AISO-Workspace).
#
# Usage, from the repository root:
#   ./scripts/setup-mac.sh                 # install + configure
#   ./scripts/setup-mac.sh --with-claude   # also install the Claude Code CLI (npm)
#   ./scripts/setup-mac.sh --with-rider    # JetBrains Rider instead of VS Code
#
# Safe to re-run: every step checks what is already there.
set -euo pipefail

WITH_CLAUDE=false
IDE=vscode
for arg in "$@"; do
  case "$arg" in
    --with-claude) WITH_CLAUDE=true ;;
    --with-rider) IDE=rider ;;
    -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORKSPACE="$HOME/AISO-Workspace"
step() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
ok()   { printf '    \033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '    \033[33m!\033[0m %s\n' "$*"; }

[[ "$(uname -s)" == "Darwin" ]] || { echo "This script is for macOS." >&2; exit 1; }

# The hardware, not the shell: a terminal running under Rosetta reports x86_64 on Apple Silicon too.
APPLE_SILICON=false
[[ "$(sysctl -n hw.optional.arm64 2>/dev/null || true)" == "1" ]] && APPLE_SILICON=true

# --- Homebrew -----------------------------------------------------------------------------------------
step "Homebrew"
USE_BREW=true
if command -v brew >/dev/null 2>&1; then
  ok "$(brew --version | head -1)"
elif $APPLE_SILICON; then
  echo "    Installing Homebrew (you may be asked for your Mac password)…"
  /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
  eval "$(/opt/homebrew/bin/brew shellenv)"
  ok "$(brew --version | head -1)"
else
  USE_BREW=false
  warn "Intel Mac without Homebrew (its installer no longer supports Intel): using the vendors' downloads"
fi

DOWNLOADS="$(mktemp -d)"
trap 'rm -rf "$DOWNLOADS"' EXIT

# vendor_app <url> <dmg|zip> "<Name>.app" <Apple team ID>: downloads an app, refuses it unless it is
# signed by that team and notarized by Apple, then copies it to /Applications.
vendor_app() {
  local url="$1" kind="$2" app="$3" team="$4" dir="$DOWNLOADS/${3%.app}"
  if [[ -d "/Applications/$app" ]]; then ok "$app already installed"; return; fi
  mkdir -p "$dir/mnt"
  echo "    Downloading $app…"
  curl -fL --progress-bar "$url" -o "$dir/download.$kind"
  if [[ "$kind" == dmg ]]; then
    hdiutil attach -nobrowse -quiet -mountpoint "$dir/mnt" "$dir/download.dmg"
    ditto "$dir/mnt/$app" "$dir/$app"
    hdiutil detach -quiet "$dir/mnt"
  else
    ditto -x -k "$dir/download.zip" "$dir"
  fi
  [[ "$(codesign -dv "$dir/$app" 2>&1)" == *"TeamIdentifier=$team"* ]] && spctl --assess --type execute "$dir/$app" 2>/dev/null \
    || { echo "    $app isn't signed by the expected vendor ($team) or isn't notarized; not installing it." >&2; exit 1; }
  ditto "$dir/$app" "/Applications/$app"
  ok "$app installed"
}

cask_installed() { brew list --cask "$1" >/dev/null 2>&1; }
install_cask() { # first cask name that exists wins (names change between Homebrew releases)
  for name in "$@"; do cask_installed "$name" && { ok "$name already installed"; return; }; done
  for name in "$@"; do
    if brew info --cask "$name" >/dev/null 2>&1; then brew install --cask "$name" && ok "$name installed"; return; fi
  done
  warn "none of: $* found in Homebrew; install it manually"
}

# --- Tools --------------------------------------------------------------------------------------------
step "Git"
if command -v git >/dev/null 2>&1 && git --version >/dev/null 2>&1; then ok "$(git --version)"
elif $USE_BREW; then brew install git; ok "git installed"
else echo "    Git is missing: run 'xcode-select --install', then re-run this script." >&2; exit 1
fi

step ".NET 10 SDK"
# dotnet-install.sh puts the SDK in ~/.dotnet; pick it up even if this shell's PATH doesn't have it yet.
[[ -x "$HOME/.dotnet/dotnet" ]] && export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks | grep -q '^10\.'; then
  ok "$(dotnet --list-sdks | grep '^10\.' | tail -1)"
else
  if $USE_BREW; then
    install_cask dotnet-sdk
    export PATH="/usr/local/share/dotnet:$PATH"
  else
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$DOWNLOADS/dotnet-install.sh"
    bash "$DOWNLOADS/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet" >/dev/null
    export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
    if ! grep -qF '.dotnet' "$HOME/.zprofile" 2>/dev/null; then
      printf '\n# .NET SDK (added by AISO setup-mac.sh)\nexport DOTNET_ROOT="$HOME/.dotnet"\nexport PATH="$PATH:$HOME/.dotnet:$HOME/.dotnet/tools"\n' >> "$HOME/.zprofile"
      ok "added ~/.dotnet to PATH in ~/.zprofile (open a new terminal to pick it up)"
    fi
  fi
  dotnet --list-sdks | grep -q '^10\.' || { echo "    .NET 10 SDK not found after install; see https://dotnet.microsoft.com/download" >&2; exit 1; }
  ok "$(dotnet --list-sdks | grep '^10\.' | tail -1)"
fi

step "Docker Desktop"
if $USE_BREW; then install_cask docker-desktop docker
else vendor_app https://desktop.docker.com/mac/main/amd64/Docker.dmg dmg Docker.app 9BNSXJN65R
fi
# Docker Desktop links its CLI into ~/.docker/bin on first start; until then use the one inside the app.
command -v docker >/dev/null 2>&1 || export PATH="$PATH:/Applications/Docker.app/Contents/Resources/bin"
if ! docker info >/dev/null 2>&1; then
  echo "    Starting Docker Desktop (first start asks you to accept its terms)…"
  open -a Docker || true
  for _ in $(seq 1 90); do docker info >/dev/null 2>&1 && break; sleep 2; done
fi
if docker info >/dev/null 2>&1; then ok "Docker engine running"; else warn "Docker isn't running yet. Open Docker Desktop, finish its setup, then re-run this script."; fi
if $APPLE_SILICON; then
  warn "Apple Silicon: SQL Server only ships for x86-64. In Docker Desktop → Settings → General, keep"
  warn "\"Use Rosetta for x86_64/amd64 emulation on Apple Silicon\" switched ON (Docker Desktop 4.25 or later)."
fi

step "Editor"
if [[ "$IDE" == "rider" ]]; then
  if $USE_BREW; then install_cask rider; else warn "install Rider from https://www.jetbrains.com/rider/download/"; fi
else
  if $USE_BREW; then install_cask visual-studio-code
  else vendor_app https://update.code.visualstudio.com/latest/darwin/stable zip "Visual Studio Code.app" UBF8T346G9
  fi
  CODE_BIN="$(command -v code || echo "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code")"
  if [[ -x "$CODE_BIN" ]]; then
    "$CODE_BIN" --install-extension ms-dotnettools.csdevkit --force >/dev/null 2>&1 && ok "C# Dev Kit extension" || warn "install the C# Dev Kit extension from the VS Code marketplace"
  fi
fi

if $WITH_CLAUDE; then
  step "Claude Code CLI"
  if command -v claude >/dev/null 2>&1; then ok "claude $(claude --version 2>/dev/null | head -1)"; else
    if ! command -v npm >/dev/null 2>&1; then
      $USE_BREW || { echo "    npm is missing: install Node.js from https://nodejs.org, then re-run." >&2; exit 1; }
      brew install node
    fi
    npm install -g @anthropic-ai/claude-code && ok "claude installed"
  fi
fi

# --- Project configuration ----------------------------------------------------------------------------
step "HTTPS development certificate"
dotnet dev-certs https --trust >/dev/null 2>&1 && ok "trusted" || warn "run 'dotnet dev-certs https --trust' and accept the Keychain prompt"

step "Restore packages and tools"
cd "$REPO_ROOT"
dotnet tool restore >/dev/null && ok "dotnet-ef tool"
dotnet restore Aiso.slnx >/dev/null && ok "NuGet packages"

step "Local secrets (stored in .NET user secrets, outside the repository)"
APPHOST=src/Aiso.AppHost
# Read once into a variable: piping into 'grep -q' can kill the writer with SIGPIPE, which pipefail
# reports as "not found" and would overwrite the password the existing SQL volume was created with.
SECRETS="$(dotnet user-secrets list --project "$APPHOST" 2>/dev/null || true)"
if [[ "$SECRETS" == *"Parameters:sql-password"* ]]; then
  ok "SQL Server password already set"
else
  # SQL Server requires upper, lower, digit and symbol. Bounded input: 'tr </dev/urandom | head' would end
  # with tr killed by SIGPIPE, and pipefail + set -e then stop the script without a message.
  SQL_PASSWORD="Aiso-$(head -c 256 /dev/urandom | LC_ALL=C tr -dc 'A-Za-z0-9' | cut -c 1-20)-9x!"
  dotnet user-secrets set "Parameters:sql-password" "$SQL_PASSWORD" --project "$APPHOST" >/dev/null && ok "generated SQL Server password"
fi
if [[ "$SECRETS" != *"Parameters:llm-api-key"* ]]; then
  # Placeholder so Aspire doesn't stop to ask. The internal gateway isn't reachable from a laptop anyway;
  # AI engine keys are entered in the dashboard's AI Engines tab.
  dotnet user-secrets set "Parameters:llm-api-key" "not-set-locally" --project "$APPHOST" >/dev/null && ok "gateway key placeholder"
fi

step "Workspace ($WORKSPACE)"
mkdir -p "$WORKSPACE/data" "$WORKSPACE/reports"
cp "$REPO_ROOT/CLAUDE.md" "$WORKSPACE/CLAUDE.md"
ok "data/, reports/ and CLAUDE.md ready"

step "Build and test"
dotnet build Aiso.slnx -v quiet -nologo | tail -3
dotnet test tests/Aiso.Tests -v quiet -nologo | tail -1

cat <<MSG

$(printf '\033[1;32m')All set.$(printf '\033[0m') Start everything (SQL Server container, migrations, dashboard, worker):

    cd "$REPO_ROOT"
    dotnet run --project src/Aiso.AppHost

Your browser opens the Aspire dashboard; click the "web" endpoint for the AISO dashboard.
You are signed in automatically as a development administrator.
The first start downloads the SQL Server image (~1.5 GB) and can take a few minutes on Apple Silicon.

Then, in the AISO dashboard → AI Engines, paste the API keys for the engines you want to use.
MSG
