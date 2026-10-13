# AV1 Video Encoder

A simple Windows app that makes videos much smaller using modern **AV1** compression. Drop a video in, pick a
quality preset, press Start. Everything runs locally: no accounts, no cloud, no telemetry, no network access.

- **AV1 only**, encoded with SVT-AV1 (software, best compression) or optionally an AMD / NVIDIA / Intel GPU.
- **Three presets**: High Quality, Balanced (default) and Smallest File, plus an optional custom quality.
- Keeps resolution, aspect ratio, frame rate (including variable frame rate), orientation, audio tracks,
  text subtitles, chapters and metadata. Anything that can't be carried over is listed after encoding.
- Safe: writes to a temporary file, verifies the result with FFprobe, and only then saves it. It never
  overwrites a file without asking, and cancelled or failed encodes leave nothing behind.

## Download

**[⬇ Download the latest version for Windows (.zip)](https://github.com/oWhiteops/av1-video-encoder/releases/latest/download/AV1VideoEncoder-win-x64.zip)**

1. Download the zip and extract it (right-click → **Extract All…**). Keep all the files together.
2. Open the extracted folder and run **AV1VideoEncoder.exe**. Nothing to install; Windows 10 or 11 (64-bit).
3. If Windows shows *"Windows protected your PC"*, click **More info** → **Run anyway**. This appears because the
   app isn't code-signed.

Older versions and what changed in each are on the [Releases](https://github.com/oWhiteops/av1-video-encoder/releases)
page and in [CHANGELOG.md](CHANGELOG.md).

## Using it

1. Run `AV1VideoEncoder.exe`.
2. Drag a video onto the window (or click the drop area to browse). MP4, MOV, MKV, AVI, WebM and most other
   formats work. **Remove** clears it so you can pick a different one.
   To compress several videos, drop or select them together, or use **Add more…**. To compress a whole folder
   (subfolders included), drop it or use **Choose a folder…** / **Add folder…**. They are encoded one after
   another with the same settings, each saved as `<name>_av1.mp4`. A video that fails is reported and the rest
   continue; Cancel stops the batch and keeps the videos already finished.
   Each video in a batch gets a quick check, then shows its own estimated size, saving and encoding time. Under the
   list are the totals: the size after compression, the encoding time, and the space saved once the originals are
   deleted. While videos are still being checked, the totals are extrapolated from the ones done so far. Videos that
   would shrink by less than 10% are counted, and **Remove them** takes them off the list. While a batch runs, a
   second progress bar shows the whole batch: percent done, which video, time elapsed, time left and space saved so far.
3. Choose a preset. **Balanced** suits almost everything.
   Once a video is loaded, a few-second quality check estimates the output size and encoding time, for example
   *Estimated output ≈ 14–21 MB · encoding time ≈ 1–2 min on this PC*, and each preset card shows its own size and
   time so you can compare them. A GPU encoder is measured at all three presets in one go. It updates as you change settings. On test
   videos about 9 in 10 results landed inside the shown ranges; other programs running make encoding slower.
   The estimates learn from your encodes: after 3 finished encodes with an encoder, they are adjusted by how far
   off they were on this PC (the median of the last 30, at most 2x either way). The data is kept in
   `%LOCALAPPDATA%\AV1 Video Encoder\estimate-history.json`; delete it to start over.
4. Check the output folder and file name (default: next to the source, named `<name>_av1.mp4`).
   **Delete the original after the new file passes a check** (Compress mode, off each time the app starts) removes
   each source once its new file is at least 10% smaller, the same length, and decodes start to end without an
   error. Originals go to the Recycle Bin, or are deleted outright on drives without one (network shares). A video
   that fails any check keeps its original, and the result says why.
5. Press **Start**. You can **Pause**, **Resume** or **Cancel** at any time; after a failure or cancel the
   button becomes **Retry**.
6. When it finishes you'll see the final size, how much was saved, and **Open folder** / **Copy file** buttons
   (Copy file puts the video on the clipboard, ready to paste into Discord or a folder; Video to Discord mode copies
   it automatically). The taskbar button shows progress and flashes when the encode ends.
   If the estimate says the output would be about as big as the source (or a batch contains AV1 videos), the app asks
   before encoding, since there is little to gain.

**Advanced options** (in the Quality card):
- *Custom quality*: CRF 10-60 (lower = better quality, bigger file) and encoder speed 2-12 (lower = slower,
  slightly smaller).
- *Encoder*: software SVT-AV1, or a hardware AV1 encoder if one was detected on this PC. Hardware is optional
  and produces larger files at the same quality; if it fails or hangs, the app falls back to software
  automatically. With a hardware encoder selected, the estimate comes from short test encodes on the GPU itself.
- *Keep audio*: untick to remove audio.

Output is **MP4** (AV1 video + AAC audio). It switches to **MKV** automatically only when the source has
image-based subtitles (e.g. Blu-ray PGS) or font attachments, which MP4 cannot store; the app tells you when
it does this.

Settings (preset, folder, encoder, audio) are remembered in `%LOCALAPPDATA%\AV1 Video Encoder\settings.json`.
The log is under **View log**, and on disk in `%LOCALAPPDATA%\AV1 Video Encoder\logs\app.log`.

## Video to Discord

Switch to **Video to Discord** at the top of the window to fit a video under an upload limit.

1. Drop a video in. A quick quality check runs in the background (a few seconds; **Cancel** or **Skip** it at
   any time, or tick *Skip the quality check* to turn it off).
2. Pick the size limit: **Discord Free (19 MB)**, or **Custom** for a larger limit such as a Nitro account's.
   MB here means the same MB Explorer shows; the exact byte count is shown beside it. 19 MB stays safely under
   Discord's free 20 MB limit (an upload test accepted 20.4 MB and rejected 21.6 MB).
3. Pick a priority: **Best Quality** (slowest), **Balanced** (default) or **Fastest**.
4. Read the recommendation. It says what it will keep or lower (resolution, frame rate) and how the result will
   likely look, and suggests trimming when that would keep much more quality. Quality is an estimate, never a
   promise.
5. Optionally set **Start** and **End** to keep only part of the video (nothing is cut unless you change them),
   or override resolution, frame rate and audio under *Adjust*.
6. Press **Start**. The output is MP4, named `<name>_discord.mp4`. Its real size is checked before it is saved;
   if it is over the limit the app retries with a lower bitrate (at most 3 attempts) and never saves a file that
   doesn't fit. If the limit can't be met without very blocky video, it stops and suggests alternatives, or you
   can choose **Encode anyway at lower quality**.

Only the main audio track is kept (downmixed to stereo when it has more channels); the app lists any tracks it
leaves out. HDR videos get a warning: colours may look washed out on most screens.

See [CHANGELOG.md](CHANGELOG.md) for what changed in each version.

## Building

Requirements: Windows 10/11 x64, the [.NET 10 SDK](https://dot.net), internet access for the first build
(to download FFmpeg and NuGet packages).

```powershell
scripts\build.ps1            # fetch FFmpeg, run all tests, publish, zip
scripts\build.ps1 -SkipTests # same without tests
```

The self-contained build (no .NET install needed on the target PC) lands in
`artifacts\AV1VideoEncoder-<version>-win-x64\` with a matching `.zip`.

To publish it, create a GitHub release and attach the zip as `AV1VideoEncoder-win-x64.zip` (no version in the
name), so the Download link above always points to the newest release:

```powershell
copy artifacts\AV1VideoEncoder-<version>-win-x64.zip AV1VideoEncoder-win-x64.zip
gh release create v<version> AV1VideoEncoder-win-x64.zip --title "<version>" --notes "See CHANGELOG.md"
```

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
| `src/Av1Encoder.Engine` | UI-independent encoding engine (probe, argument building, FFmpeg process, verification, hardware detection, size targeting, Discord recommendations). |
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
  metadata was not verified, and Dolby Vision / HDR10+ dynamic metadata is not preserved. AMD AMF keeps 10-bit
  and the BT.2020 / PQ tags (tested); other hardware encoders may drop HDR metadata (the app warns).
- Variable frame rate is detected with a fast heuristic; timestamps are always preserved regardless.
- Subtitles: text subtitles are converted to MP4 text (ASS/SSA styling is lost; MKV keeps it). Image
  subtitles need MKV. Data streams (timecode, GPS/telemetry) and cover art are not carried over and are
  listed in the result.
- Progress, remaining time and output size are estimates during encoding; the final numbers are exact.
- Pause suspends the FFmpeg process; it is not designed to survive sleep/hibernate or app restarts.
- Batches (Compress mode only) run one video at a time with the same settings.
  Video to Discord works on a single video.
- Hardware AV1 paths: AMD AMF was tested on a current AMD Radeon GPU. NVIDIA NVENC and Intel QSV argument sets follow
  vendor documentation but were not tested on real hardware; they fall back to software on failure.
- The bundled FFmpeg is about 160 MB, which dominates the download size.

- **Discord quality estimates**: the recommendation's quality wording ("good", "good to slightly soft", ...) comes
  from a short sample check calibrated on 10 real clips (`docs/CALIBRATION.md`). It describes the video as a whole:
  on uneven clips a few busy seconds can look noticeably worse than the rest, and that is not predicted.
- **Discord playback**: AV1 MP4 uploads (8- and 10-bit, landscape and portrait) were accepted and played in a test on
  a free account; the platforms tried were not recorded individually, and Discord can change its limits or players.

## License

MIT for this application's code (`LICENSE`). FFmpeg and its libraries are LGPL v3 and others; see
`THIRD-PARTY-NOTICES.md`.
