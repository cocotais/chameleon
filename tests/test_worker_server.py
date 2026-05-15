from __future__ import annotations

import time
from io import StringIO
from pathlib import Path

from media_ai_worker.server import WorkerServer


def test_initialize_returns_capabilities() -> None:
    server = WorkerServer(stdout=StringIO())

    response = server.handle(
        {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"client": "pytest"}}
    )

    assert response is not None
    assert response["result"]["capabilities"]["path_payloads"] is True
    assert "image.echo" in response["result"]["capabilities"]["tasks"]


def test_run_task_copies_file_and_reports_status(tmp_path: Path) -> None:
    input_path = tmp_path / "input.txt"
    output_dir = tmp_path / "out"
    input_path.write_text("sample", encoding="utf-8")
    server = WorkerServer(stdout=StringIO())

    response = server.handle(
        {
            "jsonrpc": "2.0",
            "id": 2,
            "method": "run_task",
            "params": {
                "kind": "image.echo",
                "input_path": str(input_path),
                "output_dir": str(output_dir),
                "provider": "local",
            },
        }
    )

    task_id = response["result"]["task_id"]

    deadline = time.time() + 2
    status = None
    while time.time() < deadline:
        status = server.handle(
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "get_status",
                "params": {"task_id": task_id},
            }
        )
        if status["result"]["state"] == "completed":
            break
        time.sleep(0.05)

    assert status is not None
    assert status["result"]["state"] == "completed"
    assert Path(status["result"]["result"]["output_path"]).read_text(encoding="utf-8") == "sample"
