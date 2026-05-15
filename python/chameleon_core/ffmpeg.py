from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .models import MediaTaskRequest, MediaTaskResult, TaskProgress

ProgressCallback = Callable[[TaskProgress], None]
CancelCallback = Callable[[], bool]

VIDEO_FORMATS = {"mp4", "mkv", "mov", "webm", "avi"}
AUDIO_FORMATS = {"mp3", "wav", "flac", "aac", "m4a", "ogg", "opus"}
IMAGE_FORMATS = {"png", "jpg", "jpeg", "webp"}
SUPPORTED_INPUTS = sorted(VIDEO_FORMATS | AUDIO_FORMATS | IMAGE_FORMATS)
SUPPORTED_OUTPUTS = sorted(VIDEO_FORMATS | AUDIO_FORMATS | IMAGE_FORMATS)


class FfmpegError(RuntimeError):
    pass


@dataclass(frozen=True)
class FfmpegTools:
    ffmpeg: str | None
    ffprobe: str | None

    @property
    def available(self) -> bool:
        return self.ffmpeg is not None and self.ffprobe is not None


def discover_tools() -> FfmpegTools:
    ffmpeg_dir = os.environ.get("CHAMELEON_FFMPEG_DIR")
    candidate_dirs = []
    if ffmpeg_dir:
        candidate_dirs.append(Path(ffmpeg_dir))

    candidate_dirs.extend(
        [
            Path.cwd() / "tools" / "ffmpeg" / platform_rid(),
            Path(__file__).resolve().parents[2] / "tools" / "ffmpeg" / platform_rid(),
        ]
    )

    ffmpeg_name = "ffmpeg.exe" if os.name == "nt" else "ffmpeg"
    ffprobe_name = "ffprobe.exe" if os.name == "nt" else "ffprobe"

    for candidate_dir in candidate_dirs:
        ffmpeg_path = candidate_dir / ffmpeg_name
        ffprobe_path = candidate_dir / ffprobe_name
        if ffmpeg_path.exists() and ffprobe_path.exists():
            return FfmpegTools(str(ffmpeg_path), str(ffprobe_path))

    return FfmpegTools(shutil.which("ffmpeg"), shutil.which("ffprobe"))


def platform_rid() -> str:
    if sys.platform == "win32":
        return "win-x64"
    if sys.platform == "darwin":
        return "osx-x64"
    return "linux-x64"


class FfmpegService:
    def __init__(self, tools: FfmpegTools | None = None) -> None:
        self.tools = tools or discover_tools()

    def capabilities(self) -> dict[str, Any]:
        return {
            "ffmpeg": {
                "available": self.tools.ffmpeg is not None,
                "path": self.tools.ffmpeg,
            },
            "ffprobe": {
                "available": self.tools.ffprobe is not None,
                "path": self.tools.ffprobe,
            },
            "supported_inputs": SUPPORTED_INPUTS,
            "supported_outputs": SUPPORTED_OUTPUTS,
        }

    def probe(self, input_path: Path) -> dict[str, Any]:
        if self.tools.ffprobe is None:
            raise FfmpegError("ffprobe was not found. Install FFmpeg or bundle it under tools/ffmpeg/win-x64.")
        if not input_path.exists():
            raise FfmpegError(f"Input file does not exist: {input_path}")

        completed = subprocess.run(
            [
                self.tools.ffprobe,
                "-v",
                "error",
                "-show_format",
                "-show_streams",
                "-print_format",
                "json",
                str(input_path),
            ],
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        if completed.returncode != 0:
            raise FfmpegError(clean_stderr(completed.stderr) or "ffprobe failed.")

        raw = json.loads(completed.stdout or "{}")
        return normalize_probe(input_path, raw)

    def convert(
        self,
        request: MediaTaskRequest,
        on_progress: ProgressCallback,
        is_cancelled: CancelCallback,
    ) -> MediaTaskResult:
        if self.tools.ffmpeg is None:
            raise FfmpegError("ffmpeg was not found. Install FFmpeg or bundle it under tools/ffmpeg/win-x64.")

        probe = self.probe(request.input_path)
        target_format = resolve_target_format(request)
        if target_format not in SUPPORTED_OUTPUTS:
            raise FfmpegError(f"Unsupported target format: {target_format}")

        output_path = resolve_output_path(request, target_format)
        output_path.parent.mkdir(parents=True, exist_ok=True)

        duration = as_float(probe.get("duration_seconds"))
        command = build_convert_command(self.tools.ffmpeg, request, output_path, target_format, probe)

        on_progress(
            TaskProgress(
                state="running",
                progress=0.0,
                phase="converting",
                message="Starting FFmpeg",
                duration_seconds=duration,
            )
        )

        stderr_lines: list[str] = []
        process = subprocess.Popen(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )

        started_at = time.monotonic()
        assert process.stdout is not None
        assert process.stderr is not None

        try:
            while True:
                if is_cancelled():
                    terminate_process(process)
                    on_progress(
                        TaskProgress(
                            state="cancelled",
                            progress=0.0,
                            phase="converting",
                            message="Cancelled",
                            duration_seconds=duration,
                        )
                    )
                    raise CancelledError()

                line = process.stdout.readline()
                if not line and process.poll() is not None:
                    break
                if not line:
                    time.sleep(0.05)
                    continue

                update = parse_progress_line(line.strip(), duration)
                if update is None:
                    continue

                elapsed = time.monotonic() - started_at
                progress, out_time, speed = update
                on_progress(
                    TaskProgress(
                        state="running",
                        progress=progress,
                        phase="converting",
                        message=f"Converting to {target_format}",
                        elapsed_seconds=elapsed,
                        duration_seconds=duration,
                        speed=speed,
                    )
                )

            stderr_lines.extend(process.stderr.readlines())
            if process.returncode != 0:
                raise FfmpegError(clean_stderr("".join(stderr_lines)) or "ffmpeg failed.")

        finally:
            if process.poll() is None:
                terminate_process(process)

        result = MediaTaskResult(
            output_path=output_path,
            log=f"Converted to {target_format}.",
            metadata={
                "target_format": target_format,
                "source": probe,
            },
        )
        on_progress(
            TaskProgress(
                state="completed",
                progress=1.0,
                phase="finalizing",
                message="Completed",
                elapsed_seconds=time.monotonic() - started_at,
                duration_seconds=duration,
                result=result,
            )
        )
        return result


def normalize_probe(input_path: Path, raw: dict[str, Any]) -> dict[str, Any]:
    format_info = raw.get("format") or {}
    streams = raw.get("streams") or []
    normalized_streams = []
    for stream in streams:
        normalized_streams.append(
            {
                "index": stream.get("index"),
                "type": stream.get("codec_type"),
                "codec": stream.get("codec_name"),
                "width": stream.get("width"),
                "height": stream.get("height"),
                "duration_seconds": as_float(stream.get("duration")),
                "bitrate": as_int(stream.get("bit_rate")),
                "sample_rate": as_int(stream.get("sample_rate")),
                "channels": stream.get("channels"),
            }
        )

    return {
        "input_path": str(input_path),
        "extension": input_path.suffix.lstrip(".").lower(),
        "container": format_info.get("format_name"),
        "duration_seconds": as_float(format_info.get("duration")),
        "size": as_int(format_info.get("size")),
        "bitrate": as_int(format_info.get("bit_rate")),
        "streams": normalized_streams,
    }


def build_convert_command(
    ffmpeg_path: str,
    request: MediaTaskRequest,
    output_path: Path,
    target_format: str,
    probe: dict[str, Any],
) -> list[str]:
    preset = request.preset or "balanced"
    options = request.options or {}
    input_path = request.input_path
    input_kind = classify_format(probe.get("extension") or input_path.suffix.lstrip("."))
    output_kind = classify_format(target_format)

    command = [
        ffmpeg_path,
        "-hide_banner",
        "-loglevel",
        "error",
        "-nostdin",
        "-y",
        "-i",
        str(input_path),
        "-progress",
        "pipe:1",
        "-nostats",
    ]

    if preset == "remux" and output_kind in {"video", "audio"}:
        command.extend(["-c", "copy"])
    elif output_kind == "audio":
        command.extend(["-vn"])
        command.extend(audio_codec_args(target_format, options))
    elif output_kind == "video":
        command.extend(video_codec_args(target_format, preset, options))
        command.extend(audio_codec_args(options.get("audio_format", audio_format_for_video(target_format)), options))
    elif output_kind == "image":
        if input_kind == "video":
            seek = str(options.get("seek", "00:00:01"))
            command[command.index("-i") : command.index("-i")] = ["-ss", seek]
            command.extend(["-frames:v", "1"])
        command.extend(image_args(target_format, options))

    resolution = options.get("resolution")
    if resolution:
        command.extend(["-vf", f"scale={resolution}"])

    fps = options.get("fps")
    if fps:
        command.extend(["-r", str(fps)])

    sample_rate = options.get("sample_rate")
    if sample_rate:
        command.extend(["-ar", str(sample_rate)])

    channels = options.get("channels")
    if channels:
        command.extend(["-ac", str(channels)])

    command.append(str(output_path))
    return command


def audio_codec_args(target_format: str, options: dict[str, Any]) -> list[str]:
    codec = options.get("audio_codec")
    bitrate = options.get("audio_bitrate")
    args: list[str] = []
    if codec:
        args.extend(["-c:a", str(codec)])
    elif target_format == "mp3":
        args.extend(["-c:a", "libmp3lame"])
    elif target_format == "wav":
        args.extend(["-c:a", "pcm_s16le"])
    elif target_format in {"aac", "m4a"}:
        args.extend(["-c:a", "aac"])
    elif target_format == "flac":
        args.extend(["-c:a", "flac"])
    elif target_format == "opus":
        args.extend(["-c:a", "libopus"])
    elif target_format == "ogg":
        args.extend(["-c:a", "libvorbis"])

    if bitrate and target_format != "wav":
        args.extend(["-b:a", str(bitrate)])
    return args


def video_codec_args(target_format: str, preset: str, options: dict[str, Any]) -> list[str]:
    codec = options.get("video_codec")
    bitrate = options.get("video_bitrate")
    crf = str(options.get("crf", crf_for_preset(preset)))
    args: list[str] = []

    if codec:
        args.extend(["-c:v", str(codec)])
    elif target_format == "webm":
        args.extend(["-c:v", "libvpx-vp9"])
    else:
        args.extend(["-c:v", "libx264", "-pix_fmt", "yuv420p"])

    if bitrate:
        args.extend(["-b:v", str(bitrate)])
    elif target_format != "webm":
        args.extend(["-crf", crf])

    if target_format != "webm":
        args.extend(["-preset", "medium"])

    return args


def image_args(target_format: str, options: dict[str, Any]) -> list[str]:
    quality = str(options.get("quality", "2"))
    if target_format in {"jpg", "jpeg"}:
        return ["-q:v", quality]
    if target_format == "webp":
        return ["-quality", str(options.get("webp_quality", "90"))]
    return []


def crf_for_preset(preset: str) -> int:
    if preset == "high":
        return 18
    if preset == "small":
        return 28
    return 23


def audio_format_for_video(target_format: str) -> str:
    if target_format == "webm":
        return "opus"
    return "aac"


def resolve_target_format(request: MediaTaskRequest) -> str:
    if request.target_format:
        return request.target_format.lower().lstrip(".")
    if request.output_path and request.output_path.suffix:
        return request.output_path.suffix.lstrip(".").lower()
    raise FfmpegError("Missing target format.")


def resolve_output_path(request: MediaTaskRequest, target_format: str) -> Path:
    if request.output_path is not None:
        return unique_path(request.output_path)

    output_dir = request.output_dir or request.input_path.parent / "Converted"
    output_path = output_dir / f"{request.input_path.stem}.{target_format}"
    return unique_path(output_path)


def unique_path(path: Path) -> Path:
    if not path.exists():
        return path

    for index in range(1, 1000):
        candidate = path.with_name(f"{path.stem} ({index}){path.suffix}")
        if not candidate.exists():
            return candidate
    raise FfmpegError(f"Could not create a unique output path for: {path}")


def classify_format(format_name: str) -> str:
    normalized = format_name.lower().lstrip(".")
    if normalized in VIDEO_FORMATS:
        return "video"
    if normalized in AUDIO_FORMATS:
        return "audio"
    if normalized in IMAGE_FORMATS:
        return "image"
    return "unknown"


def parse_progress_line(line: str, duration_seconds: float | None) -> tuple[float, float | None, str | None] | None:
    if "=" not in line:
        return None
    key, value = line.split("=", 1)
    if key == "progress" and value == "end":
        return (1.0, duration_seconds, None)
    if key not in {"out_time_ms", "out_time_us", "out_time"}:
        return None

    out_time = parse_out_time(key, value)
    if duration_seconds and duration_seconds > 0 and out_time is not None:
        return (min(max(out_time / duration_seconds, 0.0), 0.99), out_time, None)
    return (0.0, out_time, None)


def parse_out_time(key: str, value: str) -> float | None:
    try:
        if key in {"out_time_ms", "out_time_us"}:
            return float(value) / 1_000_000
        parts = value.split(":")
        if len(parts) == 3:
            hours, minutes, seconds = parts
            return int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    except ValueError:
        return None
    return None


def terminate_process(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=2)
    except subprocess.TimeoutExpired:
        process.kill()


def clean_stderr(stderr: str) -> str:
    lines = [line.strip() for line in stderr.splitlines() if line.strip()]
    return "\n".join(lines[-8:])


def as_float(value: Any) -> float | None:
    try:
        if value is None:
            return None
        return float(value)
    except (TypeError, ValueError):
        return None


def as_int(value: Any) -> int | None:
    try:
        if value is None:
            return None
        return int(float(value))
    except (TypeError, ValueError):
        return None


class CancelledError(RuntimeError):
    pass
