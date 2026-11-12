# Developing maison

For building, testing and changing maison. Using it? See the [README](../README.md).
AI coding agents: [`AGENTS.md`](../AGENTS.md) has the short version of this page.

## Build

### What you need
- Windows 10 or 11 (64-bit).
- The [.NET 10 SDK](https://dotnet.microsoft.com/download): `winget install Microsoft.DotNet.SDK.10`
- [Git](https://git-scm.com/): `winget install Git.Git`

### Build
```powershell
git clone https://github.com/GreggsNBacon/qwen-maison.git
cd qwen-maison
.\build.ps1
```
This makes **`dist\maison-<version>.exe`**, one self-contained file you can copy anywhere. The script prints `BUILD OK`
with the file's path, size and SHA-256, or `BUILD FAILED` with the reason (exit code 0 or 1). The first build downloads
packages and takes a few minutes; later ones take about a minute.

- If PowerShell says running scripts is disabled: `powershell -ExecutionPolicy Bypass -File .\build.ps1`
- The version is `<Version>` in `src\Maison.csproj`; the published exe is named after it. A plain `dotnet build` keeps
  the name `maison.exe`.
- Without the script: `dotnet publish src -c Release -o dist` does the same build.
- Settings live in `%APPDATA%\Maison\config.json` (set `MAISON_CONFIG_DIR` to use another folder). Settings from the
  older "Qwen Local Launcher" build are migrated automatically.

### With an AI agent (Qwen Code, Claude Code, …)
[`AGENTS.md`](../AGENTS.md) tells coding agents how to build and test, and the project's rules. Qwen Code reads it
automatically, and Claude Code reads it through `CLAUDE.md`. Open the cloned folder as the working folder (in maison:
**Change folder** in the simple view, or **Working folder** on the Dashboard) and ask, for example:

> Build maison and run the self-test.

The agent runs `.\build.ps1 -Test` and tells you where the exe is. Building takes a couple of minutes, so let it run.

## Test
`.\build.ps1 -Test` builds and then runs the self-test below with a throwaway settings folder. A window opens for about a
minute, screenshots of every page land in `dist\selftest\`, and the script prints `SELF-TEST OK`.

### Self-test
`maison-<version>.exe --selftest <dir> [--pull <model>] [--term <folder>] [--simple] [--accent] [--toast] [--launchopts] [--jitter <folder>] [--portal <password>]`
screenshots every page, writes `selftest.txt` and exits. Set `MAISON_CONFIG_DIR` to a scratch folder so your real
settings aren't touched.
- `--pull` installs a model through the Models page and then removes it.
- `--term` opens Claude Code, Qwen Code and PowerShell tabs, then checks that typing through xterm.js reaches the shell,
  a pop-out mirror shows the same history and its input reaches the shell, Ctrl+V pastes exactly once, idle
  notifications fire once per request, and Claude Code starts without the auto-mode warning.
- `--simple` checks the simple view and the title-bar Advanced switch.
- `--accent` checks that changing the accent recolours switches live.
- `--toast` checks that in-app messages are drawn above a terminal (asks Windows which window owns those pixels).
- `--launchopts` edits Settings → Start commands through its controls and checks the preview, the saved settings and the
  real launch, then resets.
- `--jitter` records every frame Qwen Code sends while it streams a long answer (`jitter-frames.txt`).
- `--portal` runs the web portal over real HTTPS and checks login, cookies, lockout, origin checks, state, launching and
  the mirrored terminal WebSocket.

There are no unit tests; the self-test is the test suite.

## How it works

**Launching agents.** Every session gets its own environment, so the user's normal tools are never reconfigured.
- Claude Code: `ANTHROPIC_BASE_URL=http://127.0.0.1:11434` and `ANTHROPIC_AUTH_TOKEN=ollama`, with every model slot
  mapped to the local model. Ollama serves the Anthropic Messages API natively.
- Qwen Code: `qwen --auth-type openai --openai-base-url http://127.0.0.1:11434/v1 ...`.
- Permissions: maison always passes an explicit mode (`claude --permission-mode …`, `qwen --approval-mode …`). Claude
  Code's *auto* mode isn't offered: its safety check runs on Anthropic's servers, which aren't reachable through Ollama.
- Context window: a size other than Ollama's default is set by creating a settings-only `<model>-ctx64k` variant.
- Token limits: Claude Code and Qwen Code assume a 200K window for models they don't know, so on a 32K/64K local model
  they ran until Ollama's window overflowed. maison tells each the real window (`CLAUDE_CODE_MAX_CONTEXT_TOKENS`; for
  Qwen Code a per-session settings file in `%APPDATA%\Maison\qwen` via `QWEN_CODE_SYSTEM_SETTINGS_PATH`) and caps
  replies at a quarter of it, so they compact in time. The real window is the chosen variant's size, else the loaded
  model's (`/api/ps`), else Ollama's default from its log.
- Settings → Start commands exposes every flag and variable, with a live preview of the exact command.

**Terminals.** Each tab is a Windows pseudo console (ConPTY) drawn by xterm.js (WebGL) in WebView2, so full-screen TUIs
behave as in Windows Terminal. Sessions live in `TerminalHub` and any number of viewers attach (tabs, pop-outs, the phone
portal). Output is sent in whole frames: the hub waits for a pause and holds longer while a redraw is unfinished
(an open synchronized-output block, a screen clear and reprint, or a repaint with the cursor hidden). Idle detection is
armed by Enter and fires once per request.

WebView2 draws above every WPF element in its window, so in-app messages are hosted in a separate owned, transparent
window (`ToastWindow`).

**Stats.** CPU and RAM from Windows, GPU from `nvidia-smi`, per-app GPU memory and GPU engine use from the Windows
"GPU Process Memory" / "GPU Engine" performance counters. The AI's share is Ollama's processes (`ollama`,
`llama-server`); AI power is estimated from its share of GPU load.

**Accent colour.** WPF UI builds control brushes from the accent once, so maison re-tints every theme brush that matches
an accent shade when the accent changes.

**Phone portal security.**
- HTTPS only, with a self-signed certificate kept in the Windows certificate store.
- Password stored only as a salted PBKDF2-SHA256 hash (600,000 rounds); changing it signs every device out.
- Remembered devices: a random 256-bit token in an `HttpOnly; Secure; SameSite=Strict` cookie; only its SHA-256 is stored.
- 5 wrong passwords lock that address out for 15 minutes, with a global cap of 30 failures an hour.
- Origin checks on every change and on terminal WebSockets, and a strict Content-Security-Policy.
- Internet connections refused by default (LAN plus VPN ranges such as Tailscale's 100.64/10 only).
- A random port per PC, picked on first run.

## Project layout
| Path | |
|---|---|
| `build.ps1` | Builds `dist\maison-<version>.exe`; `-Test` also runs the self-test |
| `AGENTS.md`, `CLAUDE.md` | Build/test instructions and project rules for AI coding agents |
| `src/Services/OllamaClient.cs` | Ollama REST API (version, tags, ps, create, pull with progress, generate, delete), context detection |
| `src/Services/SystemProbe.cs` | Install/runtime checks, nvidia-smi, fresh PATH lookup, start/stop/repair |
| `src/Services/Installer.cs` | Fresh-PC installers (Ollama, Claude Code, Node.js, Qwen Code) |
| `src/Services/ModelCatalog.cs` | Curated Qwen models, VRAM fit estimate, per-PC recommendation |
| `src/Services/Launcher.cs` | Per-session environment, Qwen Code settings file and commands; external terminal launch |
| `src/Services/PseudoConsole.cs` | ConPTY wrapper |
| `src/Services/TerminalHub.cs` | Shared sessions: process, history, frame batching, idle detection, size control |
| `src/Services/TermLink.cs`, `TerminalPage.cs` | One viewer ↔ session protocol; the xterm.js page (app and portal) |
| `src/Services/StatsService.cs`, `HealthAdvice.cs` | CPU/RAM/GPU and per-app VRAM/GPU polling; the VRAM-crowding warning |
| `src/Services/AppServices.cs` | Version, accent theming, idle notifications |
| `src/Services/WebPortal.cs`, `PortalSecurity.cs` | Phone portal (Kestrel HTTPS + WebSockets), auth, cert |
| `src/Services/QwenExtensions.cs`, `ModelStorage.cs` | Extension marketplaces; moving the model store |
| `src/Controls/TerminalViewer.cs` | A WebView2 view onto a shared session (tabs and pop-outs) |
| `src/Controls/ArcGauge.cs` | Animated arc gauge with the AI's share |
| `src/Views/` | Simple view, Models, Setup, Terminals, System, Extensions and Settings pages; toast window |
| `src/Models/Models.cs` | Settings (`AppConfig`, `LaunchOptions`) and small data types |
| `src/MainWindow.xaml(.cs)` | Shell, title bar, sidebar, Dashboard and the self-test (WPF-UI 4.3) |
| `src/Assets/terminal/` | xterm.js 6.0 + addons (MIT), embedded |
| `src/Assets/brand/` | App icon (`maison.ico`, all sizes) and logos |
| `branding/` | Logo source, `make-assets.ps1` to regenerate the icon and logos, and [brand notes](../branding/README.md) |

## Releasing
1. Bump `<Version>` in `src/Maison.csproj`, then `.\build.ps1 -Test`.
2. Commit and push, then
   `gh release create v<version> dist\maison-<version>.exe THIRD-PARTY-NOTICES.md --title "maison <version>"`, with the
   SHA-256 from the build output in the notes.
