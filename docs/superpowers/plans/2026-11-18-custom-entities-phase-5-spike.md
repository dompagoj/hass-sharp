# Phase 5 spike — ImageSharp on the Home Assistant image (2026-11-18)

Throwaway spike before the phase 5 plan. The spike code and raw outputs were not kept in the repository.

## Contradicts the design
**Release builds fail without a Six Labors licence key.** ImageSharp ≥ 3.2.0 (current 4.1.3), Drawing ≥ 2.2.0
(3.1.3), Fonts ≥ 2.2.0 (3.2.0), PolygonClipper 1.0.2 ship `build/*.targets` + `SixLabors.Licensing.dll`; target
`SixLabors_ValidateLicense` runs `BeforeTargets="CoreCompile"`, `ContinueOnError` only in Debug. Error: "No Six
Labors license found. Set $(SixLaborsLicenseKey), set $(SixLaborsLicenseFile), or add a 'sixlabors.lic' file…".
Checked offline (ECDSA P-256 public key in the targets); keys carry `ExpiryDateUtc` and `;AssemblyName=`. Only the
project holding the PackageReference is checked, but a Release build of anything referencing it fails on it. No
run-time check. Versions without the check have advisories: 3.1.12 → GHSA-j9gm-c75j-xc9q, GHSA-jjfr-hcj7-qf5w,
GHSA-v76p-62qx-wwq2 (high), GHSA-wmxv-xphr-5c9g (moderate); 2.1.13 → those + GHSA-j3p4-wp97-rph4.
From Six Labors' docs and posts (web search by the coordinator): open-source / source-available projects can apply
for a community key at licensing.sixlabors.com; an **assembly-scoped community key may be committed to a public
repository** (file `sixlabors.lic` in the project directory or repo root, or `SixLaborsLicenseFile`, or env
`SIXLABORS_LICENSE_KEY` with the full licence text); it is valid only for the exact AssemblyName(s) registered
(every project that directly references a Six Labors package; case-sensitive). Key lifetime and renewal: not found.
Sources: https://docs.sixlabors.com/articles/imagesharp/ , https://sixlabors.com/posts/licence-enforcement-changes/ ,
https://sixlabors.com/posts/announcing-imagesharp-400/ , https://www.nuget.org/packages/SixLabors.Licensing

## Licence text
All 3.x+ packages: "Six Labors Split License, Version 1.0, June 2022" (`<license type="file">`,
requireLicenseAcceptance). Apache-2.0 applies if "You are consuming the Work in for use in software licensed under
an Open Source or Source Available license", or "as a Transitive Package Dependency", or direct dependency of a
for-profit with "less than 1M USD annual gross revenue", or a non-profit/charity; otherwise commercial. "Once
granted, You must reference the granted license only in all documentation." Ship Apache-2.0 text + "Copyright © Six
Labors". Fonts adds THIRD-PARTY-NOTICES (FreeType FTL credit "Portions of this software are copyright © <year> The
FreeType Project (https://freetype.org). All rights reserved."; HarfBuzz MIT).

## Packages
4.1.3 / 3.1.3 / 3.2.0 restore and build for net11.0 (each ships lib/net8.0). Deps: ImageSharp → System.IO.Hashing
8.0.0; Drawing → Fonts, ImageSharp, PolygonClipper 1.0.2. Fully managed (no runtimes/, IL-only). Publish size:
ImageSharp + Hashing 2,660,616 B (zip 914,793); full set 5,142,280 B (zip 1,639,154). Release package today ≈ 81 MiB
clean (out/ 51.94 MiB + www ≈ 28.7 MiB; the local tree packs to 138 MiB because `www/` holds stale chunks — vite's
out dir is never emptied); with the full set and a font ≈ 87 MiB of 100.

## Runs on Alpine arm64 (homeassistant/home-assistant:2026.11.1, .NET 11.0.0 musl-arm64, 4 vCPU of an M2 Pro)
Invariant globalization is mandatory (no ICU in the image) and already set (`__init__.py:21`, `:49-55`,
`dotnet.runtimeconfig.json`). Median of 50 after warm-up:

| | 1280×720 | 1920×1080 |
|---|---|---|
| decode | 8.06 ms | 17.41 ms |
| draw 5 rects + 3 labels (Drawing) | 0.62 ms | 0.62 ms |
| encode q80 | 8.32 ms | 18.84 ms |
| total (worst) | 17.35 (20.28) ms | 37.23 (39.90) ms |
| resize to 640 wide / encode at 640 | 3.97 / 1.68 ms | 7.39 / 1.57 ms |
| decode→draw→resize→encode 640 | 15.48 ms | 27.27 ms |
| hand-drawn rects + 5×7 bitmap labels, total | 16.03 ms | 36.13 ms |
| TurboJPEG (already in the HA image) decode / encode | 5.03 / 3.11 ms | 10.20 / 6.57 ms |

First frame in a fresh process 117.5 ms. Peak working set 106–110 MiB (full set), 70 MiB (ImageSharp alone), 21.5
MiB empty app; pool can be capped (`MemoryAllocatorOptions.MaximumPoolSizeMegabytes`,
`ReleaseRetainedResources()`). Single-threaded codec. `DecoderOptions.TargetSize` does not help at 1080p.

## Text
No fonts in the image (`fc-list` 0). `FontCollection.Add(Stream)` from an embedded resource works; WOFF2 works.
Missing glyph → the font's empty box, no exception; `FallbackFontFamilies` exists. Home Assistant's frontend ships
`hass_frontend/static/fonts/roboto/Roboto-Regular.woff2` (64,632 B, v2.137, name table: Apache-2.0; has č ć ž š đ
Č Đ €, no emoji). Candidates (from memory, to confirm): Roboto (Apache-2.0 for 2.137), DejaVu Sans, Noto Sans
(OFL-1.1). No-font route: rectangles via `ProcessPixelRows`, 5×7 bitmap font ≈ 45 lines, 0.01–0.03 ms, blocky,
ASCII only; saves 2.48 MiB and the FreeType credit; still needs the key if ImageSharp stays.

## Load contexts
Mimic of `ScriptLoadContext` (`alc/`): a script using only the host's wrapper → ImageSharp loaded once (Default).
A script naming an ImageSharp type → private copy per script context and `MissingMethodException` when the type
crosses. Hazard: user code compiles against every loaded assembly, cached per process (`ProjectReferences.cs:11-26`,
`NodeLibraryCompiler.cs:26-29`) → exclude `SixLabors.*` and `System.IO.Hashing` from those references.

## Home Assistant camera contract (components/camera, 2026.11.1)
- Override `async_camera_image(width=None, height=None) -> bytes | None` (`entity.py:303-308`); `content_type`
  default image/jpeg.
- MJPEG without a stream source: `handle_async_mjpeg_stream` → `async_get_still_stream` loop (`entity.py:67-120`):
  calls `async_camera_image()` with no size every `frame_interval` (default and minimum 0.5 s, `const.py:20`); empty
  bytes end the stream (`:99-100`); unchanged bytes are not re-sent; **one loop per open stream**.
- `CameraEntityFeature`: `ON_OFF = 1`, `STREAM = 2`. `state` is final: recording → streaming → idle from the flags
  `is_recording`/`is_streaming` (`entity.py:332-338`); serving MJPEG does not set `is_streaming`.
- `/api/camera_proxy/{id}` → `_async_get_image(camera, 10, width, height)`; with both sizes a JPEG is downscaled by
  TurboJPEG (⅞…⅛, never below the request); timeout/empty → `HomeAssistantError("Unable to get image")`.
- `camera.snapshot` → `async_camera_image()` under 10 s.
- Source frames: `async_get_image(hass, entity_id, timeout=10, width=None, height=None) -> Image(content_type,
  content)` (`__init__.py:218`); raises `HomeAssistantError` when not set up / not found / off / no image.

## For the plan
Merge concurrent frame requests and reuse the last frame for one interval; never return empty bytes while the
source is briefly unavailable (return the last good frame); ask the source for a smaller frame when the output is
smaller; set `is_streaming` ourselves if the state should say so; cap ImageSharp's pool. Code map:
`docs/superpowers/plans/2026-11-18-custom-entities-phase-5-map.md`
(its 13 open questions are the planner's list).
