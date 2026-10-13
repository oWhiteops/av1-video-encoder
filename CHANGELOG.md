# Changelog

How AV1 Video Encoder got from a single-video compressor to what it is now. Newest first. Versions follow
`<Version>` in `src/Av1Encoder.App/Av1Encoder.App.csproj`; each release is built with `scripts\build.ps1`.

## 1.8.1 — Fixes from testing

- Folders are listed in episode order: sorted by file name, so `Season 1\S01E03` no longer comes before `S01E01`.
- In a batch, each preset card shows the whole batch's estimated size and time, so presets can be compared for a
  folder too.
- Preset cards have accessible names (screen readers and UI automation).
- The progress line names the GPU properly ("Encoding on the GPU (AMD (AMF))" instead of "Amf hardware").
- New integration test: the delete-original check passes a real encode and catches a damaged copy.

## 1.8.0 — Batch estimates and whole-batch progress

- Every video in a batch gets the quick quality check and shows its own estimated size, saving and encoding time.
- Batch totals under the list: size after compression, total encoding time, and the space saved once the
  originals are deleted. While videos are still being checked, the totals are extrapolated from the checked ones.
- Videos expected to shrink by less than 10% are counted, and **Remove them** drops them in one click.
- While a batch runs, a whole-batch progress bar shows percent done, which video, time elapsed, time left and
  space saved so far. Progress is weighted by each video's estimated encoding time, and time left is corrected by
  the actual pace. The taskbar button follows the whole batch.
- Each batch video's own "Remaining" uses its estimate instead of "calculating…".

## 1.7.0 — Compare presets before encoding

- Each preset card (High Quality, Balanced, Smallest File) shows its own estimated size and time.
- A GPU encoder is measured at all three presets in one go, so switching presets no longer starts a new
  measurement. Measurements are kept when switching between GPU and CPU.
- Fixed: the quality check line said "Quality check done" while the GPU was still being measured. It now shows
  the GPU measurement with its own progress, and Cancel or Skip stops it.
- Starting an encode stops any GPU measurement still running, so they don't compete for the GPU.

## 1.6.0 — Folders and deleting originals

- Add a whole folder (subfolders included): drop it, use **Choose a folder…** or **Add folder…**, or pass it on
  the command line.
- Optional **Delete the original after the new file passes a check** (Compress mode, off at every start). The new
  file must be the whole video, AV1, at least 10% smaller, the same length, keep its audio, and decode from start
  to end without an error. The original then goes to the Recycle Bin (deleted outright on drives without one,
  such as network shares). If any check fails, the original is kept and the result says why.

## 1.5.0 — Convenience

- **Copy file** puts the encoded video on the clipboard; Video to Discord copies it automatically, ready to paste.
- The taskbar button shows progress, paused and error states, and flashes when an encode ends.
- Warns before encoding when the estimate is about the source size; a batch offers to skip videos that are
  already AV1.
- Test coverage for NVIDIA and Intel GPU arguments, checked against the bundled FFmpeg's option list.

## 1.4.1 — Estimates learn

- After 3 finished encodes with an encoder, estimates are corrected by how far off they were on this PC (median
  of the last 30, at most 2x either way). Stored in `estimate-history.json`; delete it to start over.

## 1.4.0 — Batch compression

- Drop or select several videos (Compress mode) to encode them one after another with the same settings. A failed
  video is reported and the rest continue; Cancel keeps the videos already finished.
- **Remove** clears the loaded video; ✕ removes a video from a batch.

## 1.3.1 — GPU reliability

- A GPU encode that never starts (an intermittent driver hang) is stopped after 20 s and retried once on the
  GPU before falling back to software.

## 1.3.0 — 10-bit HDR on the GPU

- AMD GPUs now encode 10-bit HDR video (constant QP matched to the presets by VMAF) instead of falling back to
  slow software encoding; HDR tags are kept.
- With a GPU encoder selected, the size and time estimate comes from short test encodes with the exact settings.
- The progress card shows the estimate until live numbers exist, and says when the GPU failed and software took over.

## 1.2.0 — Estimates before encoding

- The quick quality check also times its samples, so the app shows estimated output size and encoding time as
  ranges before you start, for any preset or setting. Ranges were checked against 40 real encodes.
- Personal details removed from docs and comments.

## 1.1.0 — Video to Discord

- New **Video to Discord** mode: fits a video under an upload limit (Discord Free 19 MB, or a custom size) with
  two-pass encoding, a minimum-quality floor, and a recommendation of what to keep or lower (resolution, frame
  rate, trim). The real size is checked before saving and retried at a lower bitrate if needed.
- Trim, downscale, frame-rate cap and audio-track selection in the engine.
- Planner calibrated on 10 real clips.
- Oversized outputs are rejected before they are saved; retries never overwrite an existing file.
- Golden tests lock the Compress-mode FFmpeg arguments.

## 1.0.0 — First version

- Drop a video, pick High Quality, Balanced or Smallest File, press Start. AV1 via SVT-AV1, or an AMD, NVIDIA or
  Intel GPU when available, with automatic fallback to software.
- Keeps resolution, aspect ratio, frame rate, orientation, audio tracks, text subtitles, chapters and metadata;
  MP4 output, MKV only when needed.
- Writes to a temporary file, verifies it, then saves; never overwrites without asking; cancelled or failed
  encodes leave nothing behind. Pause, Resume, Cancel, Retry. Runs fully offline.
