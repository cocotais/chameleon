from __future__ import annotations

import time
from collections.abc import Callable

from .models import MediaTaskRequest, MediaTaskResult, TaskProgress
from .providers import get_provider

ProgressCallback = Callable[[TaskProgress], None]
CancelCallback = Callable[[], bool]


class MediaProcessor:
    def run(
        self,
        request: MediaTaskRequest,
        on_progress: ProgressCallback,
        is_cancelled: CancelCallback,
    ) -> MediaTaskResult:
        self._checkpoint(on_progress, is_cancelled, 0.05, "Queued")
        self._checkpoint(on_progress, is_cancelled, 0.25, "Loading provider")

        provider = get_provider(request.provider)

        self._checkpoint(on_progress, is_cancelled, 0.5, "Processing media")

        if request.kind != "image.echo":
            raise ValueError(f"Unsupported task kind: {request.kind}")

        output_path = provider.echo_image(request.input_path, request.output_dir)
        result = MediaTaskResult(output_path=output_path, log="Created sample processed output.")

        on_progress(
            TaskProgress(
                state="completed",
                progress=1.0,
                message="Completed",
                result=result,
            )
        )
        return result

    @staticmethod
    def _checkpoint(
        on_progress: ProgressCallback,
        is_cancelled: CancelCallback,
        progress: float,
        message: str,
    ) -> None:
        if is_cancelled():
            on_progress(TaskProgress(state="cancelled", progress=progress, message="Cancelled"))
            raise CancelledError()

        on_progress(TaskProgress(state="running", progress=progress, message=message))
        time.sleep(0.05)


class CancelledError(RuntimeError):
    pass

