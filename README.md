# Qwen Local Launcher (C# / WPF)

A Windows 11 style desktop app (Mica, Fluent controls) that checks that Ollama is healthy and launches
**Claude Code** or **Qwen Code** against a local Qwen model, with no API keys and no cloud calls.

- **Run:** `dist\QwenLauncher.exe`, or the "Qwen Local Launcher" desktop shortcut. Needs the .NET 10 Desktop Runtime.
- **Build:** `cd src` then `dotnet publish -c Release -o ..\dist`
- **Settings:** `%APPDATA%\QwenLauncher\config.json`

## Features
- Metric cards: server status, GPU VRAM, loaded model (% on GPU, context), last speed test.
- Health list: install, `llama-server.exe` runtime (catches the broken auto-update), server, loaded models
  with CPU-spill warnings, installed models, `claude`/`qwen` CLIs, leftover `claude-*` alias models.
  Refreshes automatically every 10 s.
- Actions: Start, Restart, Speed test, Unload, Repair Ollama (bundled script: stop, move locked files aside,
  reinstall with winget, restart), Remove old aliases.
- Launch in Windows Terminal or PowerShell, in a chosen folder:
  - **Claude Code**: per-window `ANTHROPIC_BASE_URL=http://127.0.0.1:11434`, `ANTHROPIC_AUTH_TOKEN=ollama`,
    with every model slot mapped to Qwen. Ollama serves the Anthropic Messages API natively. Your normal `claude` is untouched.
  - **Qwen Code**: `qwen --auth-type openai --openai-base-url http://127.0.0.1:11434/v1 ...`
  - **Chat**: `ollama run`
- Context window picker. The default is 64K, set by creating a settings-only `<model>-ctx64k` variant.
  On an RTX 4090, qwen3.8:27b runs 100% on GPU at 64K but spills about 28% to CPU at 128K.
- Copyable start commands for the current selection.

## Layout
| Path | |
|---|---|
| `src/Services/OllamaClient.cs` | Ollama REST API (version, tags, ps, create, generate, delete) |
| `src/Services/SystemProbe.cs` | Install/runtime checks, nvidia-smi, PATH lookup, start/stop/repair |
| `src/Services/Launcher.cs` | Env + commands for each tool, opens the terminal (`-EncodedCommand`) |
| `src/MainWindow.xaml(.cs)` | UI (WPF-UI 4.3) |
| `src/Assets/Repair-Ollama.ps1` | Embedded repair script |

`QwenLauncher.exe --selftest <dir>` writes `screenshot.png` and `selftest.txt` and then exits, for non-interactive checks.
