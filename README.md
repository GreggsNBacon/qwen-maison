# Qwen Local Launcher (C# / WPF)

A Windows 11 style desktop app (Mica, Fluent controls) that sets up and runs local AI models with Ollama and launches
**Claude Code** or **Qwen Code** against them, with no API keys and no cloud calls.

- **Run:** `dist\QwenLauncher.exe`. It is a single self-contained file, so you can copy it to any 64-bit Windows 10/11 PC
  without installing .NET. On a fresh PC it opens on the **Setup** page.
- **Build:** `cd src` then `dotnet publish -c Release -o ..\dist`
- **Settings:** `%APPDATA%\QwenLauncher\config.json`

## Pages
**Dashboard**
- Metric cards: Ollama server, loaded model (% on GPU, context), last speed test.
- GPU gauges, refreshed every 2 s: temperature (green, then amber at 75 °C, then red at 85 °C), load, power against
  the card's limit, and VRAM. Also fan speed and core clock (NVIDIA via `nvidia-smi`).
- Health list: install, `llama-server.exe` runtime (catches the broken auto-update), server, loaded models with
  CPU-spill warnings, installed models, `claude`/`qwen` CLIs. Refreshes every 10 s.
- Actions: Start, Restart, Speed test, Unload, Repair Ollama (bundled script), Remove old aliases.
- Launch, in a chosen folder, with **Open in** set to: **This app** (a tab on the Terminals page), Windows Terminal, or a PowerShell window:
  - **Claude Code**: per-window `ANTHROPIC_BASE_URL=http://127.0.0.1:11434`, `ANTHROPIC_AUTH_TOKEN=ollama`,
    with every model slot mapped to the local model. Ollama serves the Anthropic Messages API natively. Your normal `claude` is untouched.
  - **Qwen Code**: `qwen --auth-type openai --openai-base-url http://127.0.0.1:11434/v1 ...`
  - **Chat**: `ollama run`
- Context window picker. The default is 64K, set by creating a settings-only `<model>-ctx64k` variant.
- Copyable start commands for the current selection.

**Terminals**: Claude Code, Qwen Code, Chat and plain PowerShell sessions in tabs inside the app.
- Each tab is a real Windows pseudo console (ConPTY) rendered by xterm.js in WebView2, so full-screen TUIs work as they do in
  Windows Terminal: colours, Unicode, resizing, mouse and window titles.
- To copy, select text and right-click (or press Ctrl+Shift+C). To paste, right-click with nothing selected (or press Ctrl+V).
- **Pop out** reopens the current session in Windows Terminal. **Close tab** ends it. The sidebar shows the open-tab count,
  and closing the app with sessions still running asks first.
- Needs the Microsoft Edge WebView2 Runtime, which ships with Windows 11 and current Windows 10.

**Models**
- Install any Ollama model by name, with live progress (size, speed, time left) and Cancel. Partial downloads resume.
- Installed list showing size, parameter count, quantization, capabilities and age, with **Use** (set as main) and
  **Delete** buttons. Deleting a model also removes its context variants so the disk space is actually freed.
- Curated Qwen catalog with a "fits your GPU" estimate and a ★ on the pick recommended for this PC. A low-disk warning appears before large downloads.

**Setup**: one click (**Set up everything**) installs whatever is missing:
1. Ollama: official `OllamaSetup.exe` (about 1.6 GB), silent and per-user, then the server is started.
2. The recommended Qwen model for the GPU: 24 GB gets qwen3.8:27b, 10–16 GB gets qwen3.5:9b, 6–8 GB gets qwen3.5:4b, CPU-only gets 4b or 2b.
3. Claude Code: `irm https://claude.ai/install.ps1 | iex`.
4. Node.js LTS: winget, which asks for admin once.
5. Qwen Code: `npm install -g @qwen-code/qwen-code`.

Each step can also be run on its own, and installer output is shown live.

Every button and control has a tooltip.

## Layout
| Path | |
|---|---|
| `src/Services/OllamaClient.cs` | Ollama REST API (version, tags, ps, create, pull with progress, generate, delete) |
| `src/Services/SystemProbe.cs` | Install/runtime checks, nvidia-smi (temp, power, fan, clock), fresh PATH lookup, start/stop/repair |
| `src/Services/Installer.cs` | Fresh-PC installers (Ollama, Claude Code, Node.js, Qwen Code) |
| `src/Services/ModelCatalog.cs` | Curated Qwen models, VRAM fit estimate, per-PC recommendation |
| `src/Services/Launcher.cs` | Env and commands for each tool; opens the terminal (`-EncodedCommand`) |
| `src/Controls/ArcGauge.cs` | Animated arc gauge |
| `src/Services/PseudoConsole.cs` | ConPTY wrapper (CreatePseudoConsole + process + pipes) |
| `src/Controls/TerminalSession.cs` | One terminal tab: WebView2 + xterm.js connected to ConPTY |
| `src/Views/TerminalsView` | Terminals page (tabs) |
| `src/Assets/terminal/` | xterm.js 6.0, addon-fit and addon-unicode11 (MIT), embedded in the exe |
| `src/Views/ModelsView`, `src/Views/SetupView` | Models and Setup pages |
| `src/MainWindow.xaml(.cs)` | Shell, sidebar and Dashboard (WPF-UI 4.3) |
| `src/Assets/Repair-Ollama.ps1` | Embedded repair script |

## Self-test
`QwenLauncher.exe --selftest <dir> [--pull <model>]` screenshots every page, writes `selftest.txt` and exits.
With `--pull`, it also installs the model through the Models page, screenshots the download, and then deletes the model.
With `--term <folder>`, it opens Claude Code, Qwen Code and PowerShell tabs, captures them, types into PowerShell through xterm.js, and checks the output and the exit.

## Notes
- The exe is unsigned, so on another PC Windows SmartScreen shows "Windows protected your PC". Click **More info**, then **Run anyway**.
- GPU gauges need an NVIDIA card. Other GPUs still work with Ollama, but the gauges show "no NVIDIA GPU".
