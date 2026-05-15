# JSON-RPC Worker Protocol

## Transport

- JSON-RPC 2.0 over stdio.
- One compact JSON object per line.
- `stdout` is reserved for protocol messages.
- `stderr` is reserved for logs.
- Media payloads are passed by filesystem path, never as base64.

## Requests

### `initialize`

Checks worker readiness and returns FFmpeg runtime capabilities.

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":"winui"}}
```

### `probe_media`

Returns FFprobe metadata for an input media file.

```json
{"jsonrpc":"2.0","id":2,"method":"probe_media","params":{"input_path":"C:/media/input.mp4"}}
```

### `run_task`

Starts a long-running FFmpeg conversion task.

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "run_task",
  "params": {
    "kind": "media.convert",
    "input_path": "C:/media/input.wav",
    "output_dir": "C:/media/output",
    "target_format": "mp3",
    "preset": "balanced",
    "options": {}
  }
}
```

The response returns immediately with a `task_id`. Progress is delivered with `task.progress` notifications.

### `cancel_task`

Requests cooperative cancellation.

```json
{"jsonrpc":"2.0","id":3,"method":"cancel_task","params":{"task_id":"..."}}
```

### `get_status`

Returns a task snapshot.

```json
{"jsonrpc":"2.0","id":4,"method":"get_status","params":{"task_id":"..."}}
```

### `shutdown`

Asks the worker to exit cleanly.

```json
{"jsonrpc":"2.0","id":5,"method":"shutdown","params":{}}
```

## Notifications

### `task.progress`

```json
{
  "jsonrpc": "2.0",
  "method": "task.progress",
  "params": {
    "task_id": "...",
    "state": "running",
    "phase": "converting",
    "progress": 0.5,
    "message": "Processing",
    "elapsed_seconds": 3.1,
    "duration_seconds": 6.2,
    "speed": null,
    "result": null,
    "error": null
  }
}
```

Terminal states are `completed`, `cancelled`, and `failed`.

## First Supported Task Set

- `media.probe`
- `media.convert`

Supported first-pass formats are:

- Video: `mp4`, `mkv`, `mov`, `webm`, `avi`
- Audio: `mp3`, `wav`, `flac`, `aac`, `m4a`, `ogg`, `opus`
- Image: `png`, `jpg`, `jpeg`, `webp`
