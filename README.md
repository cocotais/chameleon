# WinUI3 + Python AI Multimedia Starter

Native Windows UI shell with a Python AI/media engine behind a stdio JSON-RPC boundary.

This repository intentionally avoids local HTTP, WebView, and Electron-style service stacks. The Windows app owns UI, task lifecycle, previews, cancellation, and worker restart policy. Python owns all AI and multimedia processing.

## Layout

- `src/MediaAiStudio.WinUI` - WinUI3 / Windows App SDK desktop app.
- `python/media_ai_core` - reusable Python business logic package.
- `python/media_ai_worker` - JSON-RPC 2.0 worker process over stdin/stdout.
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
python -m media_ai_worker
```

Then send one NDJSON message per line:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":"manual"}}
```

## WinUI3 app

Open `src/MediaAiStudio.WinUI/MediaAiStudio.WinUI.csproj` in Visual Studio with the Windows App SDK tooling installed, restore NuGet packages, and run the packaged app.

The app expects `python` to be on `PATH` during development and starts:

```powershell
python -m media_ai_worker
```

For MSIX production packaging, replace the development Python resolution with either:

- a bundled embeddable Python runtime under the package install directory, or
- first-run environment bootstrap into the app's local data directory.

Model files and generated media should live outside the MSIX package, usually under the app local data directory.

