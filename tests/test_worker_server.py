from __future__ import annotations

import time
import subprocess
from io import StringIO
from pathlib import Path

import pytest

from chameleon_worker.server import WorkerServer


def test_initialize_returns_ffmpeg_capabilities() -> None:
    server = WorkerServer(stdout=StringIO())

    response = server.handle(
        {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"client": "pytest"}}
    )

    assert response is not None
    assert response["result"]["capabilities"]["path_payloads"] is True
    assert "media.probe" in response["result"]["capabilities"]["tasks"]
    assert "media.convert" in response["result"]["capabilities"]["tasks"]


def test_probe_and_convert_audio_with_ffmpeg(tmp_path: Path) -> None:
    capabilities = WorkerServer(stdout=StringIO()).processor.capabilities()
    ffmpeg_path = capabilities["ffmpeg"]["path"]
    if not capabilities["ffmpeg"]["available"] or ffmpeg_path is None:
        pytest.skip("ffmpeg is not available")

    input_path = tmp_path / "tone.wav"
    output_dir = tmp_path / "out"
    subprocess.run(
        [
            ffmpeg_path,
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-f",
            "lavfi",
            "-i",
            "sine=frequency=440:duration=1",
            "-c:a",
            "pcm_s16le",
            str(input_path),
        ],
        check=True,
    )
    server = WorkerServer(stdout=StringIO())

    probe = server.handle(
        {
            "jsonrpc": "2.0",
            "id": 2,
            "method": "probe_media",
            "params": {"input_path": str(input_path)},
        }
    )
    assert probe["result"]["duration_seconds"] > 0

    response = server.handle(
        {
            "jsonrpc": "2.0",
            "id": 3,
            "method": "run_task",
            "params": {
                "kind": "media.convert",
                "input_path": str(input_path),
                "output_dir": str(output_dir),
                "target_format": "mp3",
                "preset": "balanced",
                "options": {},
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
                "id": 4,
                "method": "get_status",
                "params": {"task_id": task_id},
            }
        )
        if status["result"]["state"] == "completed":
            break
        time.sleep(0.05)

    assert status is not None
    assert status["result"]["state"] == "completed"
    output_path = Path(status["result"]["result"]["output_path"])
    assert output_path.exists()
    assert output_path.suffix == ".mp3"
