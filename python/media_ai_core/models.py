from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Literal

TaskState = Literal["queued", "running", "completed", "failed", "cancelled"]


@dataclass(frozen=True)
class MediaTaskRequest:
    kind: str
    input_path: Path
    output_dir: Path
    provider: str = "local"
    options: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class MediaTaskResult:
    output_path: Path
    thumbnail_path: Path | None = None
    log: str = ""


@dataclass(frozen=True)
class TaskProgress:
    state: TaskState
    progress: float
    message: str
    result: MediaTaskResult | None = None
    error: str | None = None

