# Bundled FFmpeg

During development, Chameleon uses `ffmpeg` and `ffprobe` from `PATH`.

For packaged builds, place the Windows x64 binaries here:

```text
tools/ffmpeg/win-x64/ffmpeg.exe
tools/ffmpeg/win-x64/ffprobe.exe
```

Alternatively, set `CHAMELEON_FFMPEG_DIR` to a directory containing both executables.
