# Instructions for AI coding agents

This file is for AI agents working on this repository (Qwen Code and most other agents read `AGENTS.md`
automatically; Claude Code reads it through `CLAUDE.md`). People: the full version is
[`docs/DEVELOPING.md`](docs/DEVELOPING.md).

## What this is
**maison**: a Windows desktop app (C# / WPF on .NET 10, WPF-UI 4.3) that installs Ollama and launches Claude Code,
Qwen Code or a chat against local models. It ships as one self-contained exe. Source is in `src/`; the **Project layout**
table in `docs/DEVELOPING.md` says which file does what, and **How it works** explains the tricky parts.

## Build
From the repository root, in PowerShell:

```powershell
.\build.ps1
```

- Output: `dist\maison-<version>.exe` (e.g. `dist\maison-0.0.5.exe`). The script prints `BUILD OK`, the exe path, size
  and SHA-256, and exits 0. On any failure it prints `BUILD FAILED: <reason>` and exits 1.
- Needs the .NET 10 SDK. If missing, the script says so; install with `winget install Microsoft.DotNet.SDK.10`.
- If PowerShell refuses to run the script: `powershell -ExecutionPolicy Bypass -File .\build.ps1`.
- The version comes from `<Version>` in `src/Maison.csproj`. Change it there, nowhere else.
- `dotnet publish` takes a minute or two (single-file, self-contained, compressed). Give the command a long timeout
  (5 minutes) instead of assuming it hung.
- `dist/`, `bin/` and `obj/` are build output and are git-ignored. Never commit them.
- A quick compile check while editing: `dotnet build src -c Release` (much faster; produces `maison.exe` under `src\bin`).

## Test
```powershell
.\build.ps1 -Test
```
Builds, then runs the app's self-test with a throwaway settings folder: it opens the window, screenshots every page to
`dist\selftest\*.png`, writes `dist\selftest\selftest.txt` and closes. `SELF-TEST OK` + exit 0 means it started, ran
and reported the right version. Look at the screenshots after UI changes.

More checks (run the exe directly; see **Self-test** in `docs/DEVELOPING.md`): `--term <folder>` (terminals, typing, paste, mirroring),
`--simple`, `--accent`, `--toast`, `--launchopts`, `--portal <password>`. Set `MAISON_CONFIG_DIR` to a scratch folder
first so the user's real settings (`%APPDATA%\Maison`) aren't changed.

There are no unit tests; the self-test is the test suite. A build with errors, or a self-test that doesn't write
`selftest.txt`, is a failure. If it crashed, the reason is in Windows Event Viewer → Application (".NET Runtime").

## Rules
- Don't run `maison.exe` from `dist\` while building: a running exe can't be replaced.
- Never edit the user's own tool settings (`~/.qwen/settings.json`, `~/.claude/settings.json`). maison passes everything
  per session (environment variables, a settings file in `%APPDATA%\Maison\qwen`).
- Never put personal data in the repo: no API keys, tokens, real e-mail addresses, user names or unredacted screenshots.
- Don't reformat or re-indent code you aren't changing; keep the style of the file you edit.
- In XAML text shown on controls, a single `_` is eaten as a keyboard-shortcut marker. Write `__` to show `_`.
- In-app terminals are WebView2, which draws above all WPF content in the window. Anything that must appear over a
  terminal (pop-ups, toasts) has to be its own window (see `src/Views/ToastWindow.cs`).
- Keep the README (written for users: what maison does for them, not how) in step with user-visible changes,
  and `docs/DEVELOPING.md` with technical ones.

## Releasing (only when the user asks)
1. Bump `<Version>` in `src/Maison.csproj`, then `.\build.ps1 -Test`.
2. Commit, push, then `gh release create v<version> dist\maison-<version>.exe THIRD-PARTY-NOTICES.md --title "maison <version>"`
   with the SHA-256 from the build output in the notes.
