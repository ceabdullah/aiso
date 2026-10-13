# Run AISO on a Mac

For development and demos. Production stays on the Windows server.

## 1. One-time setup

```bash
git clone https://github.com/ceabdullah/aiso.git
cd aiso
git checkout claude/aiso-platform-foundation   # until PR #1 is merged
./scripts/setup-mac.sh
```

The script installs only what is missing, using Homebrew:

| Tool | Why |
|---|---|
| .NET 10 SDK | builds and runs the platform |
| Docker Desktop | runs the SQL Server container |
| Visual Studio Code + C# Dev Kit | editor (`--with-rider` installs JetBrains Rider instead; Visual Studio for Mac is retired) |
| Git | source control |
| Claude Code CLI | only with `--with-claude`; not needed for the dashboard |

**Intel Macs:** Homebrew's installer no longer supports Intel. If Homebrew isn't already installed, the script
gets the tools from the vendors instead: the .NET SDK through Microsoft's `dotnet-install.sh` (into `~/.dotnet`,
added to `~/.zprofile`), and Docker Desktop and VS Code as downloads that must carry the vendor's Apple
signature and notarization before they are copied to `/Applications`. Docker Desktop and the SQL Server
image run natively on Intel, so there's no Rosetta setting to check.

It then trusts the HTTPS development certificate, restores packages, stores a generated SQL Server password in
.NET user secrets, creates `~/AISO-Workspace`, and builds and tests the solution.

**Apple Silicon (M1–M4):** SQL Server only ships for x86-64. Docker Desktop runs it through Rosetta. In
Docker Desktop → Settings → General, keep *Use Rosetta for x86_64/amd64 emulation on Apple Silicon* switched on.

## 2. Run

```bash
dotnet run --project src/Aiso.AppHost
```

The Aspire dashboard opens in your browser. Click the **web** endpoint (https://localhost:7271) for the AISO dashboard.
You're signed in automatically as a development administrator. The first start downloads the SQL Server image
(about 1.5 GB). The SQL container keeps its data between runs.

Paste the API keys for ChatGPT, Gemini, Perplexity and Claude in **AI Engines**, then click **Run audit now** on the Summary tab.

**Free demo data:** the **NVIDIA (simulated)** engine calls any OpenAI-compatible chat API (NVIDIA build.nvidia.com by
default; change the model in AI Engines, or `BaseUrl` in configuration for another provider). These models don't search the
web: they answer from training data and cite nothing, so their answers appear on the dashboard (faded rings, Answers tab)
but are left out of the visibility scores. Use them to try the dashboard, not to measure AI search visibility.
More NVIDIA-hosted models (GPT-OSS, DeepSeek, GLM, Kimi) are configured the same way and share the NVIDIA key, so
one key in the NVIDIA card switches them all on.

**Remediation plans on a Mac:** the corporate gateway isn't reachable, so plans are written by a chat model on NVIDIA
instead of `claude.exe` (`Aiso:Planner:Provider = OpenAiCompatible`, model `nvidia/nemotron-3-super-120b-a12b`). It uses the
NVIDIA key and gets the run's data files in its prompt. The Windows server keeps `claude.exe`.

## What differs from the Windows server

| | Mac (development) | Windows server |
|---|---|---|
| Workspace | `~/AISO-Workspace` | `C:\PROJECTS\LOCAL LLM DEMO` |
| Sign-in | automatic development user | Windows / Active Directory |
| Remediation plans | chat model on NVIDIA (`Aiso:Planner`) | `claude.exe` through the corporate gateway |
| API key encryption | key ring in `~/.aiso/keys` (file permissions only) | key ring encrypted with DPAPI |

To run the `claude` analysis step locally anyway, install the CLI (`./scripts/setup-mac.sh --with-claude`), point
`Aiso:Llm` at a gateway you can reach, and start with `Aiso__ClaudeCli__Enabled=true`.

## Troubleshooting

- **SQL container exits right after starting (Apple Silicon):** check the Rosetta setting above, then restart Docker Desktop.
- **Browser warns about the certificate:** run `dotnet dev-certs https --trust` and accept the Keychain prompt.
- **Port already in use:** stop the other app, or change the ports in `src/Aiso.AppHost/Properties/launchSettings.json`.
- **Start over with an empty database:** `docker rm -f $(docker ps -aq --filter name=sql) && docker volume rm aiso-sql-data`.
