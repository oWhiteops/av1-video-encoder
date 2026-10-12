# Third-party notices

AV1 Video Encoder's own code is MIT-licensed (see `LICENSE`). It ships the following third-party software,
which keeps its own license.

## FFmpeg (LGPL v3 or later)

- **What:** `ffmpeg.exe`, `ffprobe.exe` and the FFmpeg DLLs in the `ffmpeg\` folder.
- **Build:** BtbN/FFmpeg-Builds, `win64-lgpl-shared`, release branch 8.1. The exact version, download URL and
  SHA-256 are recorded in `licenses\FFmpeg-VERSION.txt`.
- **License:** GNU Lesser General Public License v3 (`--enable-version3`, no `--enable-gpl`, no non-free parts).
  Full text: `licenses\FFmpeg-LICENSE.txt`.
- **Source code:** FFmpeg <https://github.com/FFmpeg/FFmpeg> (the commit hash is in the version string, e.g.
  `n8.1.3-17-gc389840894` = commit `c389840894`); build scripts and all bundled library sources/versions:
  <https://github.com/BtbN/FFmpeg-Builds>.
- **How it is used:** AV1 Video Encoder runs FFmpeg as a separate process. It is not linked into the application,
  and the DLLs are unmodified shared libraries that can be replaced with a compatible build.

The FFmpeg DLLs statically include third-party libraries, each under its own (LGPL-compatible) license. Those
most relevant to this application:

| Library | Purpose here | License |
|---|---|---|
| SVT-AV1 | AV1 software encoding (primary encoder) | BSD-3-Clause-Clear + Alliance for Open Media Patent License 1.0 |
| dav1d | AV1 decoding (verification) | BSD-2-Clause |
| AMD AMF headers | AMD hardware AV1 encoding | MIT |
| nv-codec-headers | NVIDIA hardware AV1 encoding | MIT |
| oneVPL / libvpl | Intel hardware AV1 encoding | MIT |
| libaom, libopus, libvorbis, libvpx, libwebp, libmp3lame, zlib, libxml2, ... | Decoding of various input formats | Various BSD/MIT/LGPL-style licenses |

The complete list of libraries is the `--enable-lib*` list printed by `ffmpeg -buildconf`.

## Patents

Some formats that can be *decoded* as input (for example H.264, HEVC, AAC) may be covered by patents in some
countries. AV1, which is the only output video format, is designed to be royalty-free under the Alliance for
Open Media Patent License. If you redistribute this application commercially, review the patent situation for
your market.

## Redistribution checklist (LGPL)

When distributing builds: keep `licenses\` and this file alongside the executable, and make the corresponding
FFmpeg source available (for example by linking to, or mirroring, the exact upstream commit and BtbN build
scripts used for the release).
