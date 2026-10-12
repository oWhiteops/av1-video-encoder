# Design notes

## Environment this was built and tested on

Windows 11 Pro, a 12-core desktop CPU, a current AMD Radeon GPU with AV1 encoding, .NET SDK 10.0.401,
FFmpeg n8.1.3 (BtbN LGPL shared build) with SVT-AV1 v4.2.0.

## Technology choices

| Decision | Why |
|---|---|
| **C# / WPF on .NET 10** | Native Windows desktop, mature drag-and-drop/dialog support, and .NET 9+'s built-in Fluent theme (`ThemeMode="System"`) gives a Windows 11 look with automatic light/dark mode and no third-party UI library. Nothing in the requirements justified WinUI 3's packaging overhead. |
| **FFmpeg as a separate process** | The most capable and best-tested media pipeline. Running it out of process means a crash or hang in a codec or driver can't take the app down, cancellation is a process kill, and LGPL obligations stay simple (no linking). |
| **BtbN `win64-lgpl-shared` build, branch 8.1** | Includes `libsvtav1`, `libdav1d`, `libvmaf` and the AMF/NVENC/QSV AV1 encoders, with **no GPL code**. gyan.dev's builds are GPL (because of x264/x265, which we don't need) and the "essentials" build lacks SVT-AV1. A release branch rather than master trades a little freshness for stability. The fetch script records URL, version and SHA-256. |
| **No NuGet runtime dependencies** | The engine uses only the BCL (`System.Text.Json`, `Process`, a few Win32 calls). The only packages are the xUnit test stack. |

## Architecture

```
Av1Encoder.App (WPF)           Av1Encoder.Engine (net10.0, no UI)
  MainWindow  ── events ──▶      MediaProbe        ffprobe JSON → MediaInfo
  MainViewModel ──────────▶      FfmpegArguments   MediaInfo + EncodeRequest → argument list (pure)
  AppSettings / AppLog           EncodeJob         preflight → temp file → FFmpeg → verify → move into place
                                 FfmpegProcess     process, -progress parsing, pause/resume, job object
                                 HardwareDetector  real test encodes per GPU encoder
                                 SizeTargeting     size-limited encode, retries, quality floor, sample analysis
                                 SizePlanner       Discord recommendations (pure)
```

- **One job = one file.** `EncodeJob` holds nothing but its running process, so batch processing is a loop
  (or a queue) of jobs; nothing in the engine assumes a single file.
- **Pure argument building.** All FFmpeg arguments are built by a pure function from probe data and the
  request, so stream-handling rules are unit-tested without running FFmpeg.
- **No shell, ever.** Arguments go through `ProcessStartInfo.ArgumentList`; user paths are single list
  elements and are never concatenated into a command string. The log shows a quoted rendering only for
  diagnostics.
- **Machine-readable progress.** `-progress pipe:1 -nostats -stats_period 0.5` gives key=value blocks on
  stdout (`out_time_us`, `total_size`, `speed`, `progress=continue|end`). Stderr is kept separately (last 40
  lines) for error messages and the log.
- **UI thread is never blocked.** The view model runs jobs with `Task.Run`; progress comes back through
  `Progress<T>`, which marshals onto the dispatcher.

### Reliability details

| Concern | Handling |
|---|---|
| Partial/corrupt outputs | Encode into `<output>.<guid>.partial` in the destination folder (same volume, so the final move is atomic). On success, probe the temp file: it must contain AV1 video, every expected audio track, and at least 90% of the source duration. Only then `File.Move` it into place. Any failure or cancel deletes the temp file. Leftovers from a hard kill are removed on the next encode to that name (a temp file held open by a live encode can't be deleted, so it's left alone). |
| Overwrites | The engine refuses to overwrite unless `OverwriteExisting` is set; the UI asks first. The final move doesn't overwrite a file that appeared during encoding unless overwrite was confirmed. Output = input is rejected. |
| Orphaned FFmpeg | Every FFmpeg process is put in a Win32 job object with `KILL_ON_JOB_CLOSE`. If the app exits or crashes, Windows kills the encode. |
| Hung encoders | A watchdog kills an encode that reports no progress for 60 s (pauses excluded). Applied to hardware encoders by default: in testing, AMF on Adrenalin drivers intermittently froze during initialization (a 48-byte output and no progress), with the same arguments working on the next run. A stall becomes a normal failure, so it triggers the software fallback. |
| Hardware failures | Any hardware encoder failure (init, mid-stream error, stall) retries once with SVT-AV1 and reports that it did. |
| Pause | `NtSuspendProcess` / `NtResumeProcess` on the FFmpeg process. Elapsed and ETA exclude paused time. |
| Disk space | Before starting, compares free space with a conservative estimate (source size for CRF; bitrate × duration for size-targeted) and asks the user if it may not fit. A disk-full error during encoding gets a specific message. |
| Locked / invalid inputs | Opening the file for reading before probing turns "in use by another program" into a clear message; FFprobe failure means "corrupt or unsupported". Common FFmpeg errors (no space, permission denied, encoder init) are mapped to readable messages; the raw output goes to the log. |
| Responsiveness | FFmpeg runs at below-normal priority. |

## Stream handling

| Source stream | MP4 output (default) | MKV output |
|---|---|---|
| Main video | AV1 | AV1 |
| Extra video / cover art | not included, listed in result | same |
| Audio: AAC, MP3 | copied | copied |
| Audio: other (PCM, FLAC, AC-3, Opus, …) | AAC, 96 kbps mono / 160 kbps stereo / 64 kbps per channel above | copied, except PCM/FLAC/ALAC/TrueHD → AAC |
| Text subtitles (SRT, ASS, WebVTT, mov_text) | converted to mov_text (ASS styling lost, warned) | copied |
| Image subtitles (PGS, DVD, DVB) | can't be stored → app picks MKV automatically | copied |
| Attachments (fonts) | can't be stored → app picks MKV automatically | copied |
| Data (timecode, telemetry) | not included, listed in result | same |
| Chapters, global metadata | kept | kept |

- **Frame rate:** `-fps_mode vfr` keeps source timestamps. FFmpeg's default for MP4 is constant frame rate,
  which would duplicate or drop frames from phone (VFR) footage. A test encodes a 30 → 10 fps clip and checks
  the frame count is unchanged.
- **Rotation:** FFmpeg's autorotate applies the display matrix, so the output is stored upright. The
  displayed orientation and dimensions match the source (tested).
- **Aspect ratio / resolution:** never scaled. Sample aspect ratio is passed through (tested with anamorphic 720×480, SAR 32:27).
  Odd dimensions work (tested at 321×241).
- **Pixel format:** SVT-AV1 always encodes 10-bit 4:2:0 (`yuv420p10le`). This is standard practice: AV1 Main
  profile includes 10-bit, it compresses better even from 8-bit sources, and it reduces banding. 4:2:2/4:4:4
  sources are converted to 4:2:0 (SVT-AV1 supports only 4:2:0). Hardware encoders let FFmpeg negotiate the
  format.

## Presets: how the defaults were chosen

Benchmark: `scripts\benchmark.ps1` on the Sintel trailer (Blender Foundation, CC-BY 3.0; 1920×1080, 24 fps,
52 s, H.264 2.1 Mbps, 13.9 MB). Video only, VMAF (`libvmaf` default model) against the source.

| SVT-AV1 | CRF 26 | CRF 32 | CRF 38 | CRF 44 |
|---|---|---|---|---|
| preset 4 | 7.08 MB, VMAF 96.3 | **4.74 MB, 95.2** | 3.24 MB, 93.7 | 2.21 MB, 91.7 |
| preset 6 | 7.92 MB, 96.2 | 5.22 MB, 95.1 | 3.46 MB, 93.5 | 2.30 MB, 91.2 |
| preset 8 | 8.82 MB, 95.8 | 5.89 MB, 94.6 | 3.92 MB, 92.9 | 2.66 MB, 90.8 |

Encoding time at CRF 32 (nothing else running): preset 3 50.2 s · **4 21.7 s** · **5 20.2 s** · 6 18.1 s ·
8 12.6 s · 10 9.5 s.

| AMF (`-rc qvbr`) | level 20 | level 26 | level 32 |
|---|---|---|---|
| size, VMAF | 5.64 MB, 93.4 | 8.35 MB, 95.2 | 11.26 MB, 96.2 |

Readings:
- At equal CRF, VMAF barely changes between presets, but slower presets are smaller: preset 4 is ~9% smaller
  than 6 and ~20% smaller than 8. Preset 3 is 2.3× slower than 4 for 1.7%, which isn't worth it.
- At matched VMAF, AMF files are 40-75% larger than SVT-AV1 and, on a 12-core CPU, not much faster
  (10-20 s depending on AMF quality mode vs 20 s). Hardware is useful on weaker CPUs but is not the default.
- AMF's QVBR level is *higher = better*, the opposite of CRF.

| Preset | CRF | SVT preset | Hardware quality* | Result on the benchmark clip |
|---|---|---|---|---|
| High Quality | 26 | 4 | 20 | VMAF 96.3, video 51% of source size |
| Balanced (default) | 32 | 5 | 26 | VMAF 95.1, video 35% of source; end-to-end in the app with audio: 13.9 → 5.6 MB, 20 s |
| Smallest File | 40 | 4 | 32 | VMAF 93.1, video 20% of source, 22 s |

\* One "lower is better" 1-51 scale for all GPUs: NVENC `-cq` and QSV `-global_quality` take it directly,
AMF gets `qvbr_quality_level = 52 − value`. 20/26/32 matched SVT-AV1's VMAF at CRF 26/32/40 on AMF.
Custom CRF maps to it as `2 + 0.75 × CRF`.

VMAF ≥ 95 is commonly taken as visually transparent for typical viewing; ≈ 93 shows softening of fine detail
on close inspection, which fits "Smallest File, still clear".

**One clip isn't all footage.** CRF holds *perceived quality* roughly constant, so file size varies with
content: grainy, noisy or high-motion footage gets bigger files at the same CRF, and screen recordings and
animation get much smaller ones. That is the intended behaviour (quality first, not a fixed bitrate). The
defaults live in `QualitySettings.For` and can be overridden without a rebuild via `PresetOverrides` in
`settings.json`. SVT-AV1 runs with its default tune (VMAF/PSNR-oriented); `SvtParams` (e.g. `"tune=0"`)
allows experimenting with SVT-AV1's subjective tune.

## Video to Discord mode (engine)

Encodes to a byte-exact size limit (default 19 MiB = 19,922,944 bytes, under Discord's 20 MB free limit whether
that means 20,000,000 bytes or 20 MiB). The flow:

```csharp
var media = await MediaProbe.ProbeAsync(tools, input);
var complexity = await SizeTargeting.AnalyzeAsync(tools, media, trimStart, trimEnd);   // optional, seconds
var input = new PlanInput(media, 19_922_944, DiscordStrategy.Balanced) { Complexity = complexity, TrimStart = trimStart, TrimEnd = trimEnd };
var plan = SizePlanner.Plan(input);                       // resolution, fps, audio, score, advice, warnings
var request = plan.Apply(new EncodeRequest { InputPath = input, OutputPath = output, Quality = QualitySettings.For(QualityPreset.Balanced) }, input);
var result = await SizeTargeting.EncodeToSizeAsync(tools, request, media, 19_922_944,
    margin: plan.Settings.Margin, floorKbps: plan.FloorKbps); // TargetUnreachableException when it can't
```

### Discord compatibility spike (2026-10-12)

Files from `artifacts/discord-spike`, uploaded manually to a free Discord account. Every accepted
file was reported to play as intended; the platforms tried were not itemized, so per-platform playback is still to
be confirmed before the UI claims it.

| File | Bytes | Result |
|---|---|---|
| A: 1080p30, 10-bit AV1 | 4,687,369 | Accepted, played |
| B: 1080p30, 8-bit AV1 | 4,678,251 | Accepted, played |
| C: portrait 1080x1920, 10-bit | 4,682,784 | Accepted, played |
| D: 1080p60, 10-bit | 18,904,421 | Accepted, played |
| E: 1080p60, 10-bit | 20,429,630 | Accepted, played |
| F: 1080p60, 10-bit | 21,561,137 | Rejected: "this file exceeds the size limit" |

E being accepted means the free limit is more than 20,000,000 bytes; F being rejected puts it below
21,561,137. That fits 20 MiB (20,971,520). The 19 MiB default (19,922,944) is about 1 MB under it.
10-bit output is fine, so the 10-bit default stays.

| Strategy | SVT preset | Passes | Margin | Audio |
|---|---|---|---|---|
| Best Quality | 4 | 2 | 4% | 128 kbps |
| Balanced | 6 | 2 | 4% | 96 kbps |
| Fastest | 8 | 1 | 8% | 96 kbps |

Presets ≥ 10 are avoided: SVT-AV1 logs "Non-RTC M10+ are meant for automation tooling usage. Visual artifacts may occur."

### Size accuracy (measured with the bundled build)

Single-pass VBR vs two-pass, scratch tests:

| Clip (target) | 1-pass | 2-pass | CRF 30 + `mbr` cap |
|---|---|---|---|
| Noisy 1080p60, 20 s (5.00 MB) | −20 to −24% | +3.0% | — |
| Clean 720p30, 30 s (2.25 MB) | +5.2% | +0.7% | +27% (not a hard cap) |

Pass 1 takes 18% / 35% / 40% of a two-pass encode at preset 4 / 6 / 8 (20 s 1080p60). Pass-1 stats differ by
target bitrate; reusing them for a retry at 80% of the bitrate still landed within 0.2% of a fresh two-pass, but
retries re-run pass 1 anyway (correct by construction, ~7-9 s per retry on this clip).

Accuracy matrix (`RUN_ACCURACY=1 dotnet test --filter AccuracyMatrixTests`, 20 s synthetic 720p60 clips, 12-core desktop CPU). Every output stayed under its limit on the first attempt:

| Source | Strategy | Limit (bytes) | Output (bytes) | Of limit | Attempts | Time (s) |
| --- | --- | --- | --- | --- | --- | --- |
| easy 720p60 | BestQuality | 1,500,000 | 1,452,318 | 96.8% | 1 | 26 |
| easy 720p60 | BestQuality | 3,000,000 | 2,875,794 | 95.9% | 1 | 34 |
| easy 720p60 | BestQuality | 6,000,000 | 5,727,345 | 95.5% | 1 | 40 |
| easy 720p60 | Balanced | 1,500,000 | 1,464,822 | 97.7% | 1 | 10 |
| easy 720p60 | Balanced | 3,000,000 | 2,899,876 | 96.7% | 1 | 11 |
| easy 720p60 | Balanced | 6,000,000 | 5,753,145 | 95.9% | 1 | 12 |
| easy 720p60 | Fastest | 1,500,000 | 1,449,214 | 96.6% | 1 | 4 |
| easy 720p60 | Fastest | 3,000,000 | 2,889,233 | 96.3% | 1 | 5 |
| easy 720p60 | Fastest | 6,000,000 | 5,811,724 | 96.9% | 1 | 5 |
| noisy 720p60 | BestQuality | 1,500,000 | 1,292,817 | 86.2% | 1 | 20 |
| noisy 720p60 | BestQuality | 3,000,000 | 2,492,093 | 83.1% | 1 | 23 |
| noisy 720p60 | BestQuality | 6,000,000 | 5,596,516 | 93.3% | 1 | 26 |
| noisy 720p60 | Balanced | 1,500,000 | 1,334,672 | 89.0% | 1 | 11 |
| noisy 720p60 | Balanced | 3,000,000 | 2,549,987 | 85.0% | 1 | 12 |
| noisy 720p60 | Balanced | 6,000,000 | 5,734,525 | 95.6% | 1 | 12 |
| noisy 720p60 | Fastest | 1,500,000 | 1,083,471 | 72.2% | 1 | 5 |
| noisy 720p60 | Fastest | 3,000,000 | 2,245,165 | 74.8% | 1 | 5 |
| noisy 720p60 | Fastest | 6,000,000 | 4,041,771 | 67.4% | 1 | 5 |
| calm then noisy 720p60 | BestQuality | 1,500,000 | 1,153,012 | 76.9% | 1 | 22 |
| calm then noisy 720p60 | BestQuality | 3,000,000 | 2,354,839 | 78.5% | 1 | 26 |
| calm then noisy 720p60 | BestQuality | 6,000,000 | 4,188,126 | 69.8% | 1 | 28 |
| calm then noisy 720p60 | Balanced | 1,500,000 | 1,135,797 | 75.7% | 1 | 11 |
| calm then noisy 720p60 | Balanced | 3,000,000 | 2,375,269 | 79.2% | 1 | 12 |
| calm then noisy 720p60 | Balanced | 6,000,000 | 4,163,573 | 69.4% | 1 | 12 |
| calm then noisy 720p60 | Fastest | 1,500,000 | 1,061,159 | 70.7% | 1 | 4 |
| calm then noisy 720p60 | Fastest | 3,000,000 | 2,034,231 | 67.8% | 1 | 4 |
| calm then noisy 720p60 | Fastest | 6,000,000 | 3,960,776 | 66.0% | 1 | 4 |

Open finding: on noisy and variable-complexity synthetic content two-pass **undershoots** (69-89% of the limit), leaving quality unused; clean content lands at 96-98%. Synthetic temporal noise is a worst case, so this is re-measured on real footage in the calibration study before deciding whether an "undershoot" retry is worth its cost (it would need the first result kept as a temp file rather than finalized). One-pass (Fastest) undershoots as expected.

### File safety

- Every attempt writes `<output>.<guid>.partial` (+ `<output>.<guid>.passlog-0.log` for two-pass) beside the output.
- `EncodeRequest.MaxOutputBytes` is checked on the temp file before it is moved into place; an oversized result is
  deleted and `OutputTooLargeException` is thrown. The destination, and any file already there, is only ever
  replaced by an output that passed every check. Retries do not force overwriting.
- Temp files and pass logs are deleted on success, failure and cancel; leftovers from a hard kill are swept on the
  next encode to the same name. The window waits up to 15 s for cleanup when closed mid-encode.

### Retries and the quality floor

At most 3 attempts. After an oversized attempt only the video share is rescaled (audio is a fixed cost):
`next = kbps × (aim − audio) / (actual − audio) × 0.97`. Each strategy has a quality floor (`SizePlanner.FloorScore`);
when the next attempt would fall below it, encoding stops with `TargetUnreachableException` and the UI offers the
planner's alternatives instead of a worse file.

### Recommendations (provisional until calibrated)

`SizeTargeting.AnalyzeAsync` encodes three 2 s samples (at 20/50/80% of the selection; a short selection whole) at
CRF 32, preset 8, source size, and reports the kbps they needed. `SizePlanner` scores each candidate as
`video budget / needed kbps`, with needed kbps scaled by pixels^0.75, frame rate^0.5 and a preset factor, then
walks the ladder: original → 1080p → 720p at the source frame rate, then the same at 30 fps, then 480p.

All of these constants are **provisional** (marked in `SizePlanner.cs`): the score bands, floors, exponents,
the rough bits-per-pixel fallback, and the sample-vs-full-encode bias. They come from one clean clip (Sintel)
and must be replaced by the calibration and validation study on real footage before release.

## Licensing summary

- App code: MIT.
- FFmpeg: LGPL v3 (`--enable-version3`, no GPL, no non-free), used as an unmodified separate executable
  plus DLLs. Shipped with its license text, version/hash record, and source pointers
  (`THIRD-PARTY-NOTICES.md`). When distributing, also make the matching FFmpeg source available.
- SVT-AV1: BSD-3-Clause-Clear + AOM patent license. dav1d: BSD-2. GPU SDK headers: MIT.
- Output format AV1 is royalty-free (AOM). AAC encoding uses FFmpeg's native encoder.
- No telemetry and no network access at runtime. Downloads happen only in the build scripts.
