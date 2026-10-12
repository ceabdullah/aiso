#!/usr/bin/env bash
# One-time setup to run AISO locally on a Mac (Apple Silicon or Intel).
#
# Installs (via Homebrew, only what is missing):
#   - .NET 10 SDK            - Docker Desktop (runs the SQL Server container)
#   - Git                    - Visual Studio Code + C# Dev Kit (Visual Studio for Mac is retired)
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

# --- Homebrew -----------------------------------------------------------------------------------------
step "Homebrew"
if ! command -v brew >/dev/null 2>&1; then
  echo "    Installing Homebrew (you may be asked for your Mac password)…"
  /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
  # Apple Silicon installs to /opt/homebrew, Intel to /usr/local.
  for prefix in /opt/homebrew /usr/local; do
    [[ -x "$prefix/bin/brew" ]] && eval "$("$prefix/bin/brew" shellenv)"
  done
fi
ok "$(brew --version | head -1)"

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
command -v git >/dev/null 2>&1 && ok "$(git --version)" || { brew install git; ok "git installed"; }

step ".NET 10 SDK"
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks | grep -q '^10\.'; then
  ok "$(dotnet --list-sdks | grep '^10\.' | tail -1)"
else
  install_cask dotnet-sdk
  export PATH="/usr/local/share/dotnet:$PATH"
  dotnet --list-sdks | grep -q '^10\.' || { echo "    .NET 10 SDK not found after install; see https://dotnet.microsoft.com/download" >&2; exit 1; }
  ok "$(dotnet --list-sdks | grep '^10\.' | tail -1)"
fi

step "Docker Desktop"
install_cask docker-desktop docker
if ! docker info >/dev/null 2>&1; then
  echo "    Starting Docker Desktop (first start asks you to accept its terms)…"
  open -a Docker || true
  for _ in $(seq 1 90); do docker info >/dev/null 2>&1 && break; sleep 2; done
fi
if docker info >/dev/null 2>&1; then ok "Docker engine running"; else warn "Docker isn't running yet. Open Docker Desktop, finish its setup, then re-run this script."; fi
if [[ "$(uname -m)" == "arm64" ]]; then
  warn "Apple Silicon: SQL Server only ships for x86-64. In Docker Desktop → Settings → General, keep"
  warn "\"Use Rosetta for x86_64/amd64 emulation on Apple Silicon\" switched ON (Docker Desktop 4.25 or later)."
fi

step "Editor"
if [[ "$IDE" == "rider" ]]; then
  install_cask rider
else
  install_cask visual-studio-code
  CODE_BIN="$(command -v code || echo "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code")"
  if [[ -x "$CODE_BIN" ]]; then
    "$CODE_BIN" --install-extension ms-dotnettools.csdevkit --force >/dev/null 2>&1 && ok "C# Dev Kit extension" || warn "install the C# Dev Kit extension from the VS Code marketplace"
  fi
fi

if $WITH_CLAUDE; then
  step "Claude Code CLI"
  if command -v claude >/dev/null 2>&1; then ok "claude $(claude --version 2>/dev/null | head -1)"; else
    command -v npm >/dev/null 2>&1 || brew install node
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
if dotnet user-secrets list --project "$APPHOST" 2>/dev/null | grep -q '^Parameters:sql-password'; then
  ok "SQL Server password already set"
else
  # SQL Server requires upper, lower, digit and symbol.
  SQL_PASSWORD="Aiso-$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 20)-9x!"
  dotnet user-secrets set "Parameters:sql-password" "$SQL_PASSWORD" --project "$APPHOST" >/dev/null && ok "generated SQL Server password"
fi
if ! dotnet user-secrets list --project "$APPHOST" 2>/dev/null | grep -q '^Parameters:llm-api-key'; then
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
