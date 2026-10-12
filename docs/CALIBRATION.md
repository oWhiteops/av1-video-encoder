# Calibration and validation, 2026-10-12

Ten real clips (gameplay, phone footage incl. 4K portrait, a 13-minute ultrawide screen recording, 720p30
clips; none HDR), run with `CALIBRATION_DIR=<folder> dotnet test --filter CalibrationTests`
(1 h 46 min on a 12-core desktop CPU). FFmpeg n8.1.3 / SVT-AV1 v4.2.0-151, pinned by `BundledFfmpegIsTheCalibratedBuild`.

## What changed because of it

| Constant | Before (provisional) | Now | Evidence |
|---|---|---|---|
| Pixel exponent | 0.75 | 0.65 | 720p/480p needed pixels^0.50-0.71 of the bits, median 0.64 (screen recording excluded) |
| Frame-rate exponent | 0.5 (60->30 = 0.71x) | 0.65 (0.64x) | 60->30 fps needed 0.59-0.71x, mean 0.64 |
| Sample keyframe bias | none | sample charged one keyframe per GOP (161 frames < 50 fps, 321 at 50+) | samples read 1.94x too high on the screen recording |
| Score bands (Good / Softened) | 0.70 / 0.45 | 0.65 / 0.35 | no validation encode labelled better than it measured |
| Blocky band | 0.25 | 0.15 | **still provisional**: nothing measured below 0.3 |
| Floors (Best / Balanced / Fastest) | 0.45 / 0.30 / 0.25 | 0.35 / 0.25 / 0.20 | Best stops where clips began measuring blocky; the others are below the measured range |
| Rough bits per pixel (no analysis) | 0.03 | 0.045 | median of the clips; range 0.007-0.18 |

CRF 38 / 44 file sizes were 0.62-0.66x / 0.38-0.46x of CRF 32 on every clip except the screen recording, close
to the Sintel numbers the provisional bands came from.

## Pass criteria

| Criterion | Before | After | Met? |
|---|---|---|---|
| Sample estimate error, median <= 15% | 12% | 7% | yes |
| Sample estimate error, worst <= 35% | 94% (screen recording) | 23% | yes |
| Label matches measured band >= 80% | 16/30 (53%) | 20/30 (67%) | **no** |
| Never off by more than one band | 1 miss | 0 | yes |
| No optimistic labels (mean VMAF) | 0 | 0 | yes |
| No "Good" when the worst 2 s is poor | uneven 720p clip: worst 2 s VMAF 77.9 at "Good" | same | **no** |

Measured bands use mean VMAF: Good >= 95, slightly soft >= 90, noticeably blocky >= 80, very blocky below.
"After" scores the corrected estimate against the same clips the bands were fitted to, so it is optimistic.

**Why the label target isn't met.** Near a band edge, clips with the same score land a full band apart: at a score
of about 0.3, four clips measured blocky (VMAF 85-88) and five slightly soft (91-95). Thresholds that get 24/30
right do it by labelling six encodes better than they measured. The estimate itself is good (7% median error);
what varies is how visible the same compression is on different content.

**The uneven 720p clip.** Its three samples matched the full encode within 1%, but its worst 2-second windows scored 64 / 78 /
85 against means of 88 / 96 / 98. Sampling 11 points showed demand from 182 to 6,053 kbps, yet the dips aren't
explained by demand alone, and the spread of the three samples (1.10x) was lower than on clips with no dips
(1.56-1.66x). More samples would not have predicted it, so none were added.

## Raw results (before the changes above)

## Calibration (60 s excerpt, preset 8, video only)

| Clip | Source | CRF 32 kbps | CRF 26 / 38 / 44 size vs 32 | Pixel exponent (720p, 480p) | 30 fps factor | Sample / full |
| --- | --- | --- | --- | --- | --- | --- |
| Gameplay, portrait 60 fps A | 1080x1920 59.94 fps | 14255 | 1.48 / 0.66 / 0.44 | 0.50, 0.55 | 0.61 | 1.01 |
| Gameplay, 1440p60 | 2560x1440 59.94 fps | 40577 | 1.53 / 0.63 / 0.38 | 0.66, 0.71 | 0.59 | 0.85 |
| Screen recording, ultrawide | 2540x904 30 fps | 487 | 1.20 / 0.85 / 0.72 | -0.15, 0.42 | – | 1.94 |
| Phone, portrait 60 fps | 1080x1920 60 fps | 3791 | 1.60 / 0.64 / 0.44 | 0.61, 0.67 | 0.71 | 1.12 |
| Phone, 4K portrait A | 2160x3840 30 fps | 4439 | 1.63 / 0.64 / 0.43 | 0.64, 0.63 | – | 0.84 |
| Uneven 720p30 clip | 1280x720 29.97 fps | 4438 | 1.52 / 0.65 / 0.42 | –, 0.63 | – | 1.01 |
| 720p30 clip | 1280x720 29.97 fps | 874 | 1.51 / 0.66 / 0.46 | –, 0.63 | – | 1.12 |
| Phone, 4K portrait B | 2160x3840 30 fps | 9783 | 1.65 / 0.62 / 0.41 | 0.69, 0.70 | – | 1.04 |
| Gameplay, portrait 60 fps B | 1080x1920 59.94 fps | 5849 | 1.54 / 0.64 / 0.42 | 0.59, 0.64 | 0.65 | 0.87 |
| Phone, portrait 30 fps | 1080x1920 30 fps | 3944 | 1.59 / 0.63 / 0.41 | 0.64, 0.69 | – | 1.01 |

## Validation (whole clip, Discord encode, Balanced)

| Clip | Target | Predicted r | Predicted label | Size | Mean VMAF | 5th pct VMAF | Worst 2 s VMAF | Analysis s |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Gameplay, portrait 60 fps A | 9.0 MB | 0.30 | Blocky | 8.57 MB | 87.9 | 79.8 | 84.3 | 5 |
| Gameplay, portrait 60 fps A | 17.7 MB | 0.60 | Softened | 16.93 MB | 96.9 | 88.8 | 93.9 | 5 |
| Gameplay, portrait 60 fps A | 29.4 MB | 1.00 | Good | 28.00 MB | 98.9 | 92.1 | 96.1 | 5 |
| Gameplay, 1440p60 | 88.2 MB | 0.30 | Blocky | 84.62 MB | 85.0 | 77.2 | 77.2 | 9 |
| Gameplay, 1440p60 | 175.4 MB | 0.60 | Softened | 168.18 MB | 94.6 | 88.4 | 87.5 | 9 |
| Gameplay, 1440p60 | 291.7 MB | 1.00 | Good | 279.73 MB | 98.5 | 94.3 | 93.4 | 9 |
| Screen recording, ultrawide | 33.0 MB | 0.30 | Blocky | 23.44 MB | 96.7 | 96.1 | 94.2 | 3 |
| Screen recording, ultrawide | 56.6 MB | 0.60 | Softened | 31.69 MB | 97.2 | 97.1 | 96.7 | 3 |
| Screen recording, ultrawide | 88.1 MB | 1.00 | Good | 52.23 MB | 97.4 | 97.2 | 97.0 | 3 |
| Phone, portrait 60 fps | 6.8 MB | 0.30 | Blocky | 6.60 MB | 91.2 | 84.2 | 84.6 | 5 |
| Phone, portrait 60 fps | 13.1 MB | 0.60 | Softened | 12.62 MB | 95.6 | 91.6 | 92.9 | 5 |
| Phone, portrait 60 fps | 21.5 MB | 1.00 | Good | 20.69 MB | 97.3 | 94.4 | 94.9 | 5 |
| Phone, 4K portrait A | 7.3 MB | 0.30 | Blocky | 7.09 MB | 93.2 | 88.9 | 86.1 | 9 |
| Phone, 4K portrait A | 14.0 MB | 0.60 | Softened | 13.56 MB | 95.9 | 93.3 | 90.6 | 9 |
| Phone, 4K portrait A | 22.9 MB | 1.00 | Good | 22.30 MB | 96.9 | 94.5 | 92.1 | 9 |
| Uneven 720p30 clip | 18.6 MB | 0.30 | Blocky | 17.93 MB | 87.7 | 75.2 | 64.0 | 2 |
| Uneven 720p30 clip | 35.7 MB | 0.60 | Softened | 34.38 MB | 95.6 | 87.7 | 77.9 | 2 |
| Uneven 720p30 clip | 58.6 MB | 1.00 | Good | 56.28 MB | 98.0 | 92.8 | 85.3 | 2 |
| 720p30 clip | 2.0 MB | 0.30 | Blocky | 1.97 MB | 94.0 | 92.3 | 92.5 | 2 |
| 720p30 clip | 3.5 MB | 0.60 | Softened | 3.38 MB | 97.6 | 95.8 | 95.2 | 2 |
| 720p30 clip | 5.4 MB | 1.00 | Good | 5.24 MB | 98.9 | 97.0 | 96.5 | 2 |
| Phone, 4K portrait B | 10.4 MB | 0.30 | Blocky | 9.97 MB | 94.8 | 87.0 | 87.9 | 9 |
| Phone, 4K portrait B | 20.5 MB | 0.60 | Softened | 19.65 MB | 98.0 | 93.8 | 95.2 | 9 |
| Phone, 4K portrait B | 34.0 MB | 1.00 | Good | 32.69 MB | 99.0 | 95.8 | 97.3 | 9 |
| Gameplay, portrait 60 fps B | 3.8 MB | 0.30 | Blocky | 3.62 MB | 87.7 | 82.4 | 84.4 | 4 |
| Gameplay, portrait 60 fps B | 7.3 MB | 0.60 | Softened | 6.91 MB | 94.3 | 90.3 | 92.0 | 4 |
| Gameplay, portrait 60 fps B | 12.0 MB | 1.00 | Good | 11.40 MB | 97.1 | 93.3 | 95.2 | 4 |
| Phone, portrait 30 fps | 8.3 MB | 0.30 | Blocky | 8.05 MB | 94.1 | 89.8 | 91.0 | 4 |
| Phone, portrait 30 fps | 15.9 MB | 0.60 | Softened | 15.37 MB | 96.9 | 93.5 | 93.8 | 4 |
| Phone, portrait 30 fps | 26.1 MB | 1.00 | Good | 25.18 MB | 98.0 | 95.2 | 95.6 | 4 |
