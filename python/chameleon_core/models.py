from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Literal

TaskState = Literal["queued", "running", "completed", "failed", "cancelled"]


@dataclass(frozen=True)
class MediaTaskRequest:
    kind: str
    input_path: Path
    output_path: Path | None = None
    output_dir: Path | None = None
    target_format: str | None = None
    preset: str = "balanced"
    options: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class MediaTaskResult:
    output_path: Path
    thumbnail_path: Path | None = None
    log: str = ""
    metadata: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class TaskProgress:
    state: TaskState
    progress: float
    message: str
    phase: str = "converting"
    elapsed_seconds: float | None = None
    duration_seconds: float | None = None
    speed: str | None = None
    result: MediaTaskResult | None = None
    error: str | None = None
