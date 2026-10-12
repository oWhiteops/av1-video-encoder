# AV1 Video Encoder

A simple Windows app that makes videos much smaller using modern **AV1** compression. Drop a video in, pick a
quality preset, press Start. Everything runs locally: no accounts, no cloud, no telemetry, no network access.

- **AV1 only**, encoded with SVT-AV1 (software, best compression) or optionally an AMD / NVIDIA / Intel GPU.
- **Three presets**: High Quality, Balanced (default) and Smallest File, plus an optional custom quality.
- Keeps resolution, aspect ratio, frame rate (including variable frame rate), orientation, audio tracks,
  text subtitles, chapters and metadata. Anything that can't be carried over is listed after encoding.
- Safe: writes to a temporary file, verifies the result with FFprobe, and only then saves it. It never
  overwrites a file without asking, and cancelled or failed encodes leave nothing behind.

## Using it

1. Run `AV1VideoEncoder.exe`.
2. Drag a video onto the window (or click the drop area to browse). MP4, MOV, MKV, AVI, WebM and most other
   formats work.
3. Choose a preset. **Balanced** suits almost everything.
4. Check the output folder and file name (default: next to the source, named `<name>_av1.mp4`).
5. Press **Start**. You can **Pause**, **Resume** or **Cancel** at any time; after a failure or cancel the
   button becomes **Retry**.
6. When it finishes you'll see the final size, how much was saved, and an **Open folder** button.

**Advanced options** (in the Quality card):
- *Custom quality*: CRF 10-60 (lower = better quality, bigger file) and encoder speed 2-12 (lower = slower,
  slightly smaller).
- *Encoder*: software SVT-AV1, or a hardware AV1 encoder if one was detected on this PC. Hardware is optional
  and produces larger files at the same quality; if it fails or hangs, the app falls back to software
  automatically.
- *Keep audio*: untick to remove audio.

Output is **MP4** (AV1 video + AAC audio). It switches to **MKV** automatically only when the source has
image-based subtitles (e.g. Blu-ray PGS) or font attachments, which MP4 cannot store; the app tells you when
it does this.

Settings (preset, folder, encoder, audio) are remembered in `%LOCALAPPDATA%\AV1 Video Encoder\settings.json`.
The log is under **View log**, and on disk in `%LOCALAPPDATA%\AV1 Video Encoder\logs\app.log`.

## Building

Requirements: Windows 10/11 x64, the [.NET 10 SDK](https://dot.net), internet access for the first build
(to download FFmpeg and NuGet packages).

```powershell
scripts\build.ps1            # fetch FFmpeg, run all tests, publish, zip
scripts\build.ps1 -SkipTests # same without tests
```

The self-contained build (no .NET install needed on the target PC) lands in
`artifacts\AV1VideoEncoder-<version>-win-x64\` with a matching `.zip`.

Individual steps:

```powershell
scripts\fetch-ffmpeg.ps1                     # downloads FFmpeg into third_party\ffmpeg (once)
dotnet build Av1Encoder.slnx
dotnet test tests\Av1Encoder.Engine.Tests    # unit + integration tests (uses the real FFmpeg)
dotnet run --project src\Av1Encoder.App
scripts\benchmark.ps1 -InputFile clip.mp4    # size / speed / VMAF grid used to choose preset values
```

## Project layout

| Path | What |
|---|---|
| `src/Av1Encoder.Engine` | UI-independent encoding engine (probe, argument building, FFmpeg process, verification, hardware detection, size targeting). Reusable by the planned "Video to Discord" app. |
| `src/Av1Encoder.App` | WPF desktop app (Fluent theme, follows Windows light/dark mode). |
| `tests/Av1Encoder.Engine.Tests` | xUnit tests: pure logic plus integration tests that encode synthetic clips. |
| `scripts/` | FFmpeg fetch, build/publish, benchmark. |
| `docs/DESIGN.md` | Architecture, decisions, preset benchmark data, licensing. |

## Troubleshooting

- **"FFmpeg was not found"**: the `ffmpeg` folder next to the exe is missing. Re-extract the zip, or run
  `scripts\fetch-ffmpeg.ps1` before building from source.
- **"This file could not be read as a video"**: the file is corrupt, incomplete or not a video. Try playing
  it first.
- **"The file is in use by another program"**: close whatever is holding the video (editor, player, a sync
  tool) and try again.
- **Hardware encoder missing from the list**: the GPU or driver can't encode AV1 (AV1 encoding needs an AMD
  RX 7000+, NVIDIA RTX 40+, or Intel Arc / Core Ultra class GPU and current drivers). The log lists why each
  encoder was rejected. Software encoding always works.
- **Encoding is slow**: use Balanced or Smallest File rather than High Quality, or raise the custom speed.
  Encoding runs at below-normal priority so the PC stays responsive.
- **Output is bigger than the source**: the source was already very efficiently compressed (low bitrate).
  Try Smallest File, or keep the original.
- **The video won't play in a particular player**: see the AV1 playback note below.

## Known limitations

- **Playback compatibility**: verified playing in VLC and Microsoft Edge (Chromium, the engine Discord
  uses). Windows' modern Media Player needs Microsoft's free *AV1 Video Extension* (not tested here). The
  legacy Windows Media Player (`wmplayer.exe`) cannot play AV1 at all, 8-bit or 10-bit. Older TVs and devices
  may not support AV1.
- Software output is 10-bit AV1 (better compression, less banding). Some very old hardware AV1 decoders only
  handle 8-bit.
- HDR: BT.2020 / PQ / HLG colour signalling is preserved with SVT-AV1 (tested). Static mastering-display
  metadata was not verified, and Dolby Vision / HDR10+ dynamic metadata is not preserved. Hardware encoders
  may drop HDR metadata (the app warns).
- Variable frame rate is detected with a fast heuristic; timestamps are always preserved regardless.
- Subtitles: text subtitles are converted to MP4 text (ASS/SSA styling is lost; MKV keeps it). Image
  subtitles need MKV. Data streams (timecode, GPS/telemetry) and cover art are not carried over and are
  listed in the result.
- Progress, remaining time and output size are estimates during encoding; the final numbers are exact.
- Pause suspends the FFmpeg process; it is not designed to survive sleep/hibernate or app restarts.
- One file at a time. The engine is built so batch processing is a loop over jobs, but there is no batch UI.
- Hardware AV1 paths: AMD AMF was tested on a current AMD Radeon GPU. NVIDIA NVENC and Intel QSV argument sets follow
  vendor documentation but were not tested on real hardware; they fall back to software on failure.
- The bundled FFmpeg is about 160 MB, which dominates the download size.

## License

MIT for this application's code (`LICENSE`). FFmpeg and its libraries are LGPL v3 and others; see
`THIRD-PARTY-NOTICES.md`.
