# JSON-RPC Worker Protocol

## Transport

- JSON-RPC 2.0 over stdio.
- One compact JSON object per line.
- `stdout` is reserved for protocol messages.
- `stderr` is reserved for logs.
- Media payloads are passed by filesystem path, never as base64.

## Requests

### `initialize`

Checks worker readiness and returns runtime capabilities.

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":"winui"}}
```

### `run_task`

Starts a long-running media task.

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "run_task",
  "params": {
    "kind": "image.echo",
    "input_path": "C:/media/input.png",
    "output_dir": "C:/media/output",
    "provider": "local",
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
    "progress": 0.5,
    "message": "Processing",
    "result": null,
    "error": null
  }
}
```

Terminal states are `completed`, `cancelled`, and `failed`.

