from __future__ import annotations

from collections.abc import Callable
from pathlib import Path
from typing import Any

from .ffmpeg import CancelledError, FfmpegService
from .models import MediaTaskRequest, MediaTaskResult, TaskProgress

ProgressCallback = Callable[[TaskProgress], None]
CancelCallback = Callable[[], bool]


class MediaProcessor:
    def __init__(self, ffmpeg: FfmpegService | None = None) -> None:
        self.ffmpeg = ffmpeg or FfmpegService()

    def capabilities(self) -> dict[str, Any]:
        return {
            "tasks": ["media.probe", "media.convert"],
            "cancellation": True,
            "path_payloads": True,
            **self.ffmpeg.capabilities(),
        }

    def probe(self, input_path: Path) -> dict[str, Any]:
        return self.ffmpeg.probe(input_path)

    def run(
        self,
        request: MediaTaskRequest,
        on_progress: ProgressCallback,
        is_cancelled: CancelCallback,
    ) -> MediaTaskResult:
        if request.kind != "media.convert":
            raise ValueError(f"Unsupported task kind: {request.kind}")

        if is_cancelled():
            on_progress(TaskProgress(state="cancelled", progress=0.0, phase="queued", message="Cancelled"))
            raise CancelledError()

        return self.ffmpeg.convert(request, on_progress, is_cancelled)
