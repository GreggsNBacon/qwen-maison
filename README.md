<p align="center">
  <picture>
    <source media="(prefers-color-scheme: light)" srcset="docs/logo-light.png">
    <img src="docs/logo-dark.png" alt="maison — the Qwen-tissential AI home" width="560">
  </picture>
</p>

**maison** is a Windows app that sets up and runs AI models on your own GPU with [Ollama](https://ollama.com), and launches
**Claude Code** or **Qwen Code** against them. It needs no API keys and makes no cloud calls, so everything stays at home.

- **Run:** `dist\maison.exe`. It's one self-contained file, so you can copy it to any 64-bit Windows 10/11 PC without
  installing .NET. On a fresh PC it opens on **Setup** and installs everything else for you.
- **Build:** `cd src` then `dotnet publish -c Release -o ..\dist`
- **Settings:** `%APPDATA%\Maison\config.json`. Settings from the older "Qwen Local Launcher" build are migrated automatically.

![Dashboard](docs/screenshot-dashboard.png)

## Pages

**Dashboard**
- Cards: Ollama server status, the loaded model (with % on GPU and context size), and the last speed test.
- GPU gauges, refreshed every 2 s: temperature (green, then amber at 75 °C, then red at 85 °C), load, power against the
  card's limit, VRAM, fan speed and core clock (NVIDIA via `nvidia-smi`).
- Health checks: Ollama install, runtime files (`llama-server.exe`, which catches a broken auto-update), server, loaded
  models with CPU-spill warnings, installed models, and the `claude`/`qwen` CLIs. Refreshes every 10 s.
- Actions: Start, Restart, Speed test, Unload, Repair Ollama (bundled script), and Remove old aliases.
- Launch **Claude Code**, **Qwen Code** or **Chat**. **Open in** chooses where: this app (a tab), Windows Terminal, or a PowerShell window.
  - Claude Code gets a per-session `ANTHROPIC_BASE_URL=http://127.0.0.1:11434` and `ANTHROPIC_AUTH_TOKEN=ollama`, with every
    model slot mapped to the local model. Ollama serves the Anthropic Messages API natively, and your normal `claude` is untouched.
  - Qwen Code runs `qwen --auth-type openai --openai-base-url http://127.0.0.1:11434/v1 ...`.
- Context-window picker. The default is 64K, set by creating a settings-only `<model>-ctx64k` variant.
- Copyable start commands for the current selection.

**Terminals**: Claude Code, Qwen Code, Chat and plain PowerShell sessions in tabs inside the app.

![Qwen Code running inside maison](docs/screenshot-terminal.png)

- Each tab is a real Windows pseudo console (ConPTY) shown by xterm.js in WebView2. Full-screen TUIs work as they do in
  Windows Terminal, including colours, Unicode, resizing, mouse and titles.
- To copy, select text and right-click (or press Ctrl+Shift+C). To paste, right-click with nothing selected (or press Ctrl+V).
- **Pop out** reopens the session in Windows Terminal. Closing the app with sessions running asks first.

**Models**
- Install any Ollama model by name, with live progress (size, speed, time left) and Cancel. Interrupted downloads resume.
- Installed list with size, parameter count, quantization, capabilities and age. **Use** sets a model as the main model,
  and **Delete** removes it along with its context variants, so the disk space is actually freed.
- A curated Qwen catalog showing whether each model fits your GPU, with a ★ on the recommended pick and a warning when disk space is low.

**Setup**: **Set up everything** installs whatever is missing:
1. Ollama: the official `OllamaSetup.exe` (about 1.6 GB), installed silently for the current user, after which the server starts.
2. The recommended Qwen model for the GPU: qwen3.8:27b for 24 GB, qwen3.5:9b for 10–16 GB, qwen3.5:4b for 6–8 GB, and 4b or 2b on CPU.
3. Claude Code: `irm https://claude.ai/install.ps1 | iex`.
4. Node.js LTS: winget (asks for admin once).
5. Qwen Code: `npm install -g @qwen-code/qwen-code`.

Every button and control has a tooltip.

## Project layout
| Path | |
|---|---|
| `src/Services/OllamaClient.cs` | Ollama REST API (version, tags, ps, create, pull with progress, generate, delete) |
| `src/Services/SystemProbe.cs` | Install/runtime checks, nvidia-smi, fresh PATH lookup, start/stop/repair |
| `src/Services/Installer.cs` | Fresh-PC installers (Ollama, Claude Code, Node.js, Qwen Code) |
| `src/Services/ModelCatalog.cs` | Curated Qwen models, VRAM fit estimate, per-PC recommendation |
| `src/Services/Launcher.cs` | Per-session environment and commands; external terminal launch |
| `src/Services/PseudoConsole.cs` | ConPTY wrapper |
| `src/Controls/TerminalSession.cs` | One terminal tab (WebView2 + xterm.js ↔ ConPTY) |
| `src/Controls/ArcGauge.cs` | Animated arc gauge |
| `src/Views/` | Models, Setup and Terminals pages |
| `src/MainWindow.xaml(.cs)` | Shell, sidebar and Dashboard (WPF-UI 4.3) |
| `src/Assets/terminal/` | xterm.js 6.0 + addons (MIT), embedded |
| `src/Assets/brand/` | App icon (`maison.ico`, all sizes) and logos |
| `branding/` | Logo source (`maison-icon.svg`), `make-assets.ps1` to regenerate the icon and logos, and the design explorations |

## Brand
- Name: **maison**, French for "house", with the pun on **ai**. Always lowercase, with *ai* in the gradient.
- Tagline: "the **Qwen**-tissential AI home", with *Qwen* in the gradient.
- Icon: a lowercase *ai* in Segoe UI Variable Display Bold under a gradient roof. The i's dot is coral, like a lit window.
- Gradient: `#E8825F` → `#C76AD8` → `#7C5CFF`.

## Self-test
`maison.exe --selftest <dir> [--pull <model>] [--term <folder>]` screenshots every page, writes `selftest.txt` and exits.
- `--pull` installs a model through the Models page and then removes it.
- `--term` opens Claude Code, Qwen Code and PowerShell tabs, types into one through xterm.js, and checks the output and the exit.

## Notes
- The exe is unsigned. On another PC, SmartScreen may show "Windows protected your PC": click **More info**, then **Run anyway**.
- The GPU gauges need an NVIDIA card. In-app terminals need the WebView2 Runtime, which ships with Windows 11.
- Not affiliated with Anthropic, Alibaba/Qwen or Ollama. Those names refer to the tools that maison launches.
