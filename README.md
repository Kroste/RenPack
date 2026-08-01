# RenPack

[![CI](https://github.com/Kroste/RenPack/actions/workflows/ci.yml/badge.svg)](https://github.com/Kroste/RenPack/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Kroste/RenPack)](https://github.com/Kroste/RenPack/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Open, pack and unpack Ren'Py archives (`.rpa`) — cross-platform desktop app for Windows and Linux (C# / .NET 10 / Avalonia 12).

<!-- Screenshot: add docs/screenshot.png once the UI is settled -->

## Features

- **Archive browser:** open `.rpa` files via button or drag & drop. Sortable file list with size column, filter by name.
- **Extract:** all files or a specific selection (checkboxes) into a target folder — the archive's directory structure is preserved.
- **Pack:** turn a folder into a new archive in **RPA-3.0** format (the version Ren'Py reads natively).
- **Formats:** reads **RPA-2.0**, **RPA-3.0** and **RPA-3.2** (including XOR obfuscation); writes RPA-2.0/3.0/3.2.
- **Save editor:** open Ren'Py save files (`.save`), see the screenshot and metadata (slot name, time, Ren'Py version), and **edit every store variable in place** — double-click or F2 changes the value (supports simple types: int, float, str, bool, None). Modifications get a gold marker (●); *Save* overwrites the original, *Save as …* writes a copy, *Revert* rolls everything back. The rollback log and every unknown Ren'Py class stay byte-identical — Ren'Py loads the edited save exactly like the original.
- **`.rpyc` decompiler:** turn compiled Ren'Py scripts (`.rpyc`) back into Ren'Py source — from inside the app, **no Python**, **no need to start the game**. Single files or entire folders (recursive, with progress). Covers dialogue, labels, menus, if/else, show/scene/hide, jump/call, python blocks, transforms with ATL bodies, screens and translations. Preserves store-variable references and reconstructs transform parameter names from the ATL body.
- **AI translation:** the save editor can translate variable names (which are usually short abbreviations or English words) into your target language via AI, shown in a separate column. The original name stays unchanged (it is the splice anchor when saving). Supported providers: **Ollama** (local, no API key), **Anthropic Claude**, **OpenAI ChatGPT**, **Google Gemini**, **Mistral** and any **OpenAI-compatible** endpoint (Groq, OpenRouter, LM Studio, …). API keys are stored encrypted (Windows: DPAPI; Linux/macOS: AES with a machine/user binding). Ollama models can be downloaded directly from the app (progress bar).
- **Localization:** the UI ships in **English (default), German, French and Russian**. Switch languages live under *Settings → Application language* — no restart needed.
- **Modern card-based UI:** dark theme with gold highlights, sectioned toolbars, live-focus rings, subtle row hovers. Every window inherits the same style library.
- **Progress & logs:** progress bar while extracting/packing, comprehensive log with automatic masking of passwords/tokens.
- **Preview pane** (v0.6): text, image, **video and audio** files (`.rpy`, `.py`, `.json`, `.png`, `.jpg`, `.webm`, `.mp4`, `.mp3`, `.ogg`, …) are shown live next to the file list — no need to extract first. Video shows a still frame grabbed via `ffmpeg`; a button opens the file in your system's default player (VLC, mpv, Windows Media Player). Requires `ffmpeg` in PATH (Bazzite: `ffmpeg-free`, Windows: `winget install Gyan.FFmpeg`).
- **Recent files, hotkeys, context menu, double-click** (v0.6): Ctrl+O/P/E/F, F5, Esc, Ctrl+Shift+A/D. Recent-archives dropdown next to *Open*. Right-click a file for *Extract single* / *Copy path*. Double-click extracts a single file.
- **Undo/Redo in the save editor** (v0.6): Ctrl+Z / Ctrl+Y roll individual value edits back and forth without discarding everything.
- **Save diff** (v0.6): compare two save files side by side — added, removed, modified variables with filters.
- **Batch extract** (v0.6): pick multiple `.rpa` archives, each goes into its own subfolder.
- **In-app log viewer** (v0.6): inspect `logs/RenPack.log` right from the About window (filter, refresh, open folder).
- **System tray + single-instance guard** (v0.6): minimising drops the window into the tray, closing quits properly; a second launch raises the running instance.
- **Self-update** (v0.6): the About dialog can download and install a new release with one click (Windows ZIP, Linux AppImage or tar.gz) and restart the app automatically. No silent install — you always confirm.
- **Update check:** queries GitHub Releases (proxy-aware) and offers a hint when a new version is available.

## Installation

Prebuilt packages live on the [Releases page](https://github.com/Kroste/RenPack/releases):

**Windows:** download `RenPack-X.Y.Z-win-x64.zip`, extract it, run `RenPack.exe`. No installation required (self-contained, .NET is bundled).

**Linux (AppImage, recommended):** download `RenPack-X.Y.Z-x86_64.AppImage`, mark it executable and run it:

```bash
chmod +x RenPack-*-x86_64.AppImage
./RenPack-*-x86_64.AppImage
```

**Linux (tar.gz):** extract `RenPack-X.Y.Z-linux-x64.tar.gz` and run `./RenPack`.

## Usage

**Open an archive:** click *📂 Open* and pick a `.rpa` file — or drag it onto the window. The included files appear in the list; the filter box narrows them by name.

**Extract everything:** click *⬇ Extract all* and choose a destination folder. Every file gets extracted there, subfolders included.

**Extract only some files:** tick the desired files in the list (the ✓ column). The status bar shows the current selection count. Then click *⬇ Extract selection*.

**Create a new archive:** click *📦 Pack …*, pick a source folder, and choose the target `.rpa` file. RenPack packs the folder as an RPA-3.0 archive.

**Edit a save file:** click *💾 Save editor*, pick a `.save` file, and browse the store variables. Double-click a row (or press F2) to change a value. The gold ● marker shows unsaved changes; *💾 Save* writes them into the original file, *💾 Save as …* writes a copy, and *↩ Revert* rolls them back.

Ren'Py saves typically live under `~/.renpy/<GameName>/` (Linux) or `%APPDATA%/RenPy/<GameName>/` (Windows) and are named e.g. `1-1-LT1.save`. Some games keep them next to the game under `game/saves/`.

**What works today:** editing simple values (int, float, str, bool, None). The rollback log and every unknown Ren'Py class stay byte-identical — Ren'Py loads the edited file exactly like the original.

**What doesn't work yet:** adding new variables, type changes (int → str), editing lists or dictionaries. Coming in a later version.

**Decompile a `.rpyc` file:** click *🔓 .rpyc → .rpy* to decompile one or more compiled Ren'Py scripts back into source. For an entire directory tree use *🔓 Folder …* — RenPack walks it recursively and shows progress. Output files land next to the originals.

**Translate variable names:** in the save editor, click *🌐 Translate*. First-time users open *⚙ Settings*, pick an AI provider, and (for cloud providers) enter the API key. Ollama is the privacy-friendly default and needs no key.

**Change the UI language:** open *⚙ Settings → Application language* and pick English, Deutsch, Français or Русский. The whole interface updates live.

## Settings

- **Application language:** the UI language.
- **AI provider:** the app-wide default provider for translating variable names.
- **Target language:** the natural language the AI translates *into* (independent from the UI language — you can keep the UI in English but translate save variables into German).
- **Provider-specific fields:** endpoint, model name and API key (cloud providers only). Ollama can also list installed models and pull new ones directly from the settings window.

Settings live under `%APPDATA%/RenPack/settings.json` (Windows) or `$XDG_CONFIG_HOME/RenPack/settings.json` (Linux). API keys are stored encrypted, never in plain text.

For Ollama specifically, a locally running server is required:

```bash
# https://ollama.com
ollama serve
```

Models can be pulled from the command line (`ollama pull gemma3:1b`) or **directly from RenPack** — *⬇ Download* opens a progress window.

The *Test connection* button in Settings sends a real test translation — handy for checking model + key before hitting *Save*.

## Logs

RenPack logs to `logs/renpack.log` (rotated daily, 14 days retention). Passwords and API tokens are masked automatically. If you file a bug report, attach the relevant log lines — they save a lot of guesswork.

## Development

```bash
dotnet build   # build
dotnet test    # tests
dotnet run --project RenPack
```

Release: the VS Code task *release (tag + push)* checks the git state, sets the tag and triggers the GitHub Action that builds every package.

## License

MIT — see [LICENSE](LICENSE).

---

☕ Enjoying the tool? [Buy me a coffee](https://buymeacoffee.com/kroste)
