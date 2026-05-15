# Chameleon

Native media format converter powered by an FFmpeg-first Python worker behind a stdio JSON-RPC boundary.

This repository intentionally avoids local HTTP, WebView, and Electron-style service stacks. The Windows app owns UI, queue management, task lifecycle, cancellation, and logs. Python owns FFmpeg discovery, probing, conversion, progress parsing, and cancellation.

## Layout

- `src/Chameleon.WinUI` - WinUI3 / Windows App SDK desktop app.
- `python/chameleon_core` - reusable Python business logic package.
- `python/chameleon_worker` - JSON-RPC 2.0 worker process over stdin/stdout.
- `tests` - Python protocol and worker tests.
- `docs/protocol.md` - transport and message contract.

## Run the Python worker tests

```powershell
python -m pytest tests
```

If `pytest` is not installed:

```powershell
python -m pip install pytest
```

## Try the worker manually

```powershell
$env:PYTHONPATH="python"
python -m chameleon_worker
```

Then send one NDJSON message per line:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":"manual"}}
```

Chameleon currently supports:

- `probe_media`
- `run_task` with `kind: "media.convert"`

The first supported format set covers common audio, video, and image formats handled by FFmpeg.

## WinUI3 app

Open `src/Chameleon.WinUI/Chameleon.WinUI.csproj` in Visual Studio with the Windows App SDK tooling installed, restore NuGet packages, and run the packaged app.

The app expects `python` to be on `PATH` during development and starts:

```powershell
python -m chameleon_worker
```

It also expects `ffmpeg` and `ffprobe` to be discoverable from `PATH` during development.

For MSIX production packaging, replace the development Python resolution with either:

- a bundled embeddable Python runtime under the package install directory, or
- first-run environment bootstrap into the app's local data directory.

For production FFmpeg packaging, place `ffmpeg.exe` and `ffprobe.exe` under `tools/ffmpeg/win-x64/` in the app layout or set `CHAMELEON_FFMPEG_DIR`.

Generated media should live outside the MSIX package, usually under a user-selected output folder or the source file's `Converted` sibling folder.
