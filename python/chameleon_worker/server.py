from __future__ import annotations

import json
import sys
import threading
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, TextIO

from chameleon_core import MediaProcessor, MediaTaskRequest, TaskProgress
from chameleon_core.ffmpeg import CancelledError

from .jsonrpc import error, notification, success


@dataclass
class TaskRecord:
    request: MediaTaskRequest
    state: str = "queued"
    progress: float = 0.0
    message: str = "Queued"
    result: dict[str, Any] | None = None
    error: str | None = None
    phase: str = "queued"
    cancel_requested: bool = False


class WorkerServer:
    def __init__(
        self,
        stdin: TextIO | None = None,
        stdout: TextIO | None = None,
        stderr: TextIO | None = None,
    ) -> None:
        self.stdin = stdin or sys.stdin
        self.stdout = stdout or sys.stdout
        self.stderr = stderr or sys.stderr
        self.processor = MediaProcessor()
        self.tasks: dict[str, TaskRecord] = {}
        self._lock = threading.Lock()
        self._write_lock = threading.Lock()
        self._shutdown = False

    def run(self) -> int:
        for raw_line in self.stdin:
            line = raw_line.strip()
            if not line:
                continue

            try:
                message = json.loads(line)
                response = self.handle(message)
            except Exception as exc:
                response = error(None, -32700, "Parse or dispatch error", str(exc))

            if response is not None:
                self._write(response)

            if self._shutdown:
                break

        return 0

    def handle(self, message: dict[str, Any]) -> dict[str, Any] | None:
        message_id = message.get("id")
        method = message.get("method")
        params = message.get("params") or {}

        if message.get("jsonrpc") != "2.0":
            return error(message_id, -32600, "Invalid JSON-RPC version")

        if method == "initialize":
            return success(
                message_id,
                {
                    "worker": "chameleon_worker",
                    "protocol": "jsonrpc-2.0-ndjson",
                    "capabilities": self.processor.capabilities(),
                },
            )

        if method == "probe_media":
            return self._probe_media(message_id, params)

        if method == "run_task":
            return self._run_task(message_id, params)

        if method == "cancel_task":
            return self._cancel_task(message_id, params)

        if method == "get_status":
            return self._get_status(message_id, params)

        if method == "shutdown":
            self._shutdown = True
            return success(message_id, {"ok": True})

        return error(message_id, -32601, f"Method not found: {method}")

    def _probe_media(self, message_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        try:
            input_path = Path(params["input_path"])
            return success(message_id, self.processor.probe(input_path))
        except KeyError as exc:
            return error(message_id, -32602, f"Missing required parameter: {exc.args[0]}")
        except Exception as exc:
            return error(message_id, -32010, "Media probe failed", str(exc))

    def _run_task(self, message_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        try:
            output_path = params.get("output_path")
            output_dir = params.get("output_dir")
            request = MediaTaskRequest(
                kind=str(params["kind"]),
                input_path=Path(params["input_path"]),
                output_path=Path(output_path) if output_path else None,
                output_dir=Path(output_dir) if output_dir else None,
                target_format=(
                    str(params["target_format"])
                    if params.get("target_format") is not None
                    else None
                ),
                preset=str(params.get("preset", "balanced")),
                options=dict(params.get("options") or {}),
            )
        except KeyError as exc:
            return error(message_id, -32602, f"Missing required parameter: {exc.args[0]}")

        task_id = str(uuid.uuid4())
        record = TaskRecord(request=request)
        with self._lock:
            self.tasks[task_id] = record

        thread = threading.Thread(target=self._execute_task, args=(task_id,), daemon=True)
        thread.start()
        return success(message_id, {"task_id": task_id})

    def _execute_task(self, task_id: str) -> None:
        def on_progress(progress: TaskProgress) -> None:
            result = None
            if progress.result is not None:
                result = {
                    "output_path": str(progress.result.output_path),
                    "thumbnail_path": (
                        str(progress.result.thumbnail_path)
                        if progress.result.thumbnail_path is not None
                        else None
                    ),
                    "log": progress.result.log,
                    "metadata": progress.result.metadata,
                }

            with self._lock:
                record = self.tasks[task_id]
                record.state = progress.state
                record.progress = progress.progress
                record.message = progress.message
                record.phase = progress.phase
                record.result = result
                record.error = progress.error

            self._write(
                notification(
                    "task.progress",
                    {
                        "task_id": task_id,
                        "state": progress.state,
                        "progress": progress.progress,
                        "phase": progress.phase,
                        "message": progress.message,
                        "elapsed_seconds": progress.elapsed_seconds,
                        "duration_seconds": progress.duration_seconds,
                        "speed": progress.speed,
                        "result": result,
                        "error": progress.error,
                    },
                )
            )

        def is_cancelled() -> bool:
            with self._lock:
                return self.tasks[task_id].cancel_requested

        try:
            self.processor.run(self.tasks[task_id].request, on_progress, is_cancelled)
        except CancelledError:
            pass
        except Exception as exc:
            with self._lock:
                record = self.tasks[task_id]
                record.state = "failed"
                record.error = str(exc)
                record.message = "Failed"

            self._write(
                notification(
                    "task.progress",
                    {
                        "task_id": task_id,
                        "state": "failed",
                        "phase": "failed",
                        "progress": self.tasks[task_id].progress,
                        "message": "Failed",
                        "elapsed_seconds": None,
                        "duration_seconds": None,
                        "speed": None,
                        "result": None,
                        "error": str(exc),
                    },
                )
            )

    def _cancel_task(self, message_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        task_id = str(params.get("task_id", ""))
        with self._lock:
            record = self.tasks.get(task_id)
            if record is None:
                return error(message_id, -32004, f"Unknown task: {task_id}")
            record.cancel_requested = True
        return success(message_id, {"ok": True})

    def _get_status(self, message_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        task_id = str(params.get("task_id", ""))
        with self._lock:
            record = self.tasks.get(task_id)
            if record is None:
                return error(message_id, -32004, f"Unknown task: {task_id}")
            payload = {
                "task_id": task_id,
                "state": record.state,
                "phase": record.phase,
                "progress": record.progress,
                "message": record.message,
                "result": record.result,
                "error": record.error,
            }
        return success(message_id, payload)

    def _write(self, payload: dict[str, Any]) -> None:
        with self._write_lock:
            self.stdout.write(json.dumps(payload, separators=(",", ":")) + "\n")
            self.stdout.flush()
