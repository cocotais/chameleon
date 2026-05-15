from __future__ import annotations

from pathlib import Path
from shutil import copy2


class ProviderError(RuntimeError):
    pass


class LocalProvider:
    name = "local"

    def echo_image(self, input_path: Path, output_dir: Path) -> Path:
        if not input_path.exists():
            raise ProviderError(f"Input file does not exist: {input_path}")

        output_dir.mkdir(parents=True, exist_ok=True)
        suffix = input_path.suffix or ".bin"
        output_path = output_dir / f"{input_path.stem}.processed{suffix}"
        copy2(input_path, output_path)
        return output_path


class CloudProvider:
    name = "cloud"

    def echo_image(self, input_path: Path, output_dir: Path) -> Path:
        raise ProviderError("Cloud provider is not configured yet.")


def get_provider(name: str) -> LocalProvider | CloudProvider:
    if name == "local":
        return LocalProvider()
    if name == "cloud":
        return CloudProvider()
    raise ProviderError(f"Unknown provider: {name}")

