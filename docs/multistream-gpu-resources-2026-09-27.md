# Multistream GPU resources, 2026-09-27

This change keeps the selected stream quality and frame rate, hardware decoding,
audio selection, native chat, and replay behavior. It reduces video readback work
and extra decoder allocations in the verified VLC 3.0.23 configuration. The CPU
constraint is checked using the complete observed application process tree.

The baseline is the existing working tree captured at
`2026-09-27T13:43:34.064044Z`, including its earlier uncommitted changes, at Git
revision `12e0adbb8476aa3ede6353d1be7701f47761a358`. Its 1,220 files were archived
with a SHA-256 manifest before this task. Earlier CPU and chat optimizations are
already in this baseline. Existing unrelated processes were left alone.

The final-core controlled HLS comparisons use the same Release measurement
assembly and three alternating before/after pairs. Each value is the median of
three trials. The synthetic source remains 1920x1080 at 60 fps with animated
native chat in every tile:

| Streams | CPU cores | Dedicated GPU memory, MiB | Shared GPU memory, MiB | GPU 3D, % | Video decode, % | Minimum displayed fps |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 4 | 0.799 → 0.437 (-45.3%) | 402.5 → 376.7 (-6.4%) | 366.7 → 80.5 (-78.0%) | 11.75 → 4.97 (-57.7%) | 44.92 → 37.95 | 59.73 → 59.86 |
| 8 | 2.208 → 0.423 (-80.9%) | 771.5 → 688.8 (-10.7%) | 726.6 → 153.7 (-78.8%) | 6.15 → 3.58 (-41.8%) | 58.28 → 58.19 | 59.72 → 59.73 |

All controlled HLS trials completed with zero lost pictures or audio buffers.
CPU use fell in both configurations. The renderer change leaves the selected
source resolution, frame rate, and hardware decoding intact; the video-decode
counter is recorded separately from the rendering and memory reductions.

The final-core live comparison used Twitch `eslcs` and `esl_dota2`, plus Kick
`xqc` and `deenthegreat`, with `best` quality in all four panes. Each feed
reported 1920x1080 in every trial. Values below are medians from three
alternating pairs:

| CPU cores | Dedicated GPU memory, MiB | Shared GPU memory, MiB | GPU 3D, % | Video decode, % | Minimum displayed fps |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 0.789 → 0.368 (-53.3%) | 402.7 → 376.5 (-6.5%) | 365.7 → 78.6 (-78.5%) | 11.64 → 5.93 (-49.0%) | 38.43 → 34.36 | 59.83 → 59.83 |

All live trials had zero lost pictures or audio buffers, active chat on all
four tabs, and no playback child processes left running after cleanup. Live
content and GPU clocks vary between runs, so its video-decode percentages are
observations rather than evidence that the renderer reduced decoding work.

The old renderer downloads the full-resolution DXVA2 picture, converts it to RGB,
and scales it into the visible tile. The new path receives the decoder's opaque
surface directly, uses a DXVA2 video processor for color conversion and scaling,
and downloads only the displayed RGB rectangle. The final GDI canvas and filtered
chat composition remain the same. There is no Direct3D presentation swapchain
over the WPF window. This follows Microsoft's
[DXVA2 video processing](https://learn.microsoft.com/en-us/windows/win32/medfound/dxva-video-processing)
and [render-target readback](https://learn.microsoft.com/en-us/windows/win32/api/d3d9/nf-d3d9-idirect3ddevice9-getrendertargetdata)
APIs.

Pool pictures share a 16x16 NV12 descriptor surface that communicates the device
to VLC. Decoded frames retain their own full-size reference surfaces in VLC's
picture context. One FFmpeg frame worker avoids extra surfaces for parallel frame
workers while keeping the codec's reference-picture allocation. Diagnostic logs
confirmed 24 to 18 decoder surfaces per 1080p H.264 input; no reference-picture
limit was reduced. VLC's allocation rule is in
[`directx_va.c`](https://github.com/videolan/vlc/blob/3.0.23/modules/codec/avcodec/directx_va.c).
The policy also applies to prepared replay inputs. Software/custom-overlay
compatibility mode retains its earlier replay worker policy.

The opaque picture layout is private to VLC, so the app enables this path only
with the verified bundled plugin and VLC 3.0.23. The bundled core is selected
only when both installed VLC DLL hashes match the pinned pair; other VLC builds
keep their own core. Its import shim resolves `WaitOnAddress` through
`kernelbase.dll` for this exact 3.0.23 pair. The native option defaults off.
Progressive eight-bit SDR frames carry their BT.601/709 and full/limited-range
metadata into the video processor. Unsupported formats or capabilities retain
the ordinary converter. A readback or device-identity failure uses a lazily
created VLC download/conversion chain, preserving subsequent frames and chat.
The existing replay output gate still hides preroll until a seek is confirmed.

Utilization is reported separately for rendering and decoding. Keeping the same
source resolution and cadence preserves the compressed-video decoding workload;
this is not a decoder-complexity optimization. GPU clock changes can also change
the reported percentage. Whole-device NVIDIA utilization, clocks, power, and
memory are recorded independently of the process counters. Engine percentages
must not be added together or presented as a whole-desktop Task Manager reading;
Microsoft describes its [busiest-engine reporting](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/).

The controlled workload uses the production WPF window, Streamlink HTTP
transports, libVLC, the bundled plugin, and the same native animated-chat helper.
Every source is the same advancing local HLS broadcast at 1920x1080, 60 fps,
with moving H.264 video and AAC audio. The window is 1360x900. Each fresh process
has 15 seconds of warmup and 30 seconds of measurement. Runs alternate
before/after, after/before, before/after at both four and eight streams.
Captures and per-pane chat visibility checks happen after measurement.

The live workload used the production playback, chat, replay, and viewer
services with four public Twitch/Kick streams. It used 20 seconds of warmup and
60 seconds of measurement, also in three alternating pairs. The selected
quality stayed `best` in both versions. Network and source content can change
between live runs; the local HLS workload provides the repeatable comparison.

Both versions use the identical measurement assembly
`a5a0a6534f43f5ba9ef8d35fddbe8cb1b638debeea2b2d4d5e38f0bf454ac91a` in the
final-core HLS and live comparisons. The updated Release output contains the
verified VLC core described above. CPU cores means CPU seconds divided by
elapsed seconds. CPU, memory, and I/O include the host and its observed
descendants. GPU engine and allocation counters are restricted to that same
process tree. Dedicated GPU memory is VRAM; shared GPU memory is reported
separately. Neither is the application's private RAM.
NVIDIA clock samples are aligned to each recorded UTC measurement interval,
excluding startup, captures, and teardown. No builds or other playback tests run
during the comparisons.

The machine is an Intel Core i9-13900K with an RTX 4080 (16 GiB), NVIDIA driver
616.56, Windows build 19044, VLC 3.0.23, .NET SDK 10.0.302, and Streamlink 8.5.
The inherited execution context reports 16 available logical CPUs, while WMI
reports 24 for the machine; results use CPU cores without normalizing to either.
GPU power management and process affinity are unchanged.

Validation completed:

- `scripts/dev.ps1 Check -NoRestore`: 1,306 tests passed, 254 expected
  interactive/opt-in skips; formatting, tooling syntax, and bundled dependency
  verification passed. The locked offline restore used `NuGetAudit=false`
  because the NuGet vulnerability feed was unavailable; the Release
  warning-as-error build then completed with zero warnings and errors.
- The pinned VLC 3.0.23 core built twice reproducibly at SHA-256
  `6efb3c92094ddfe9aca032910a4fadf1a5ce53488488720ca84bba56722291a2`.
  Native dependency verification also checked that the runtime hash pin matches
  the provenance manifest.
- `scripts/test-gdi-hardware.ps1`: real-GPU checks for all four color
  matrix/range combinations, fractional resizing, source crops, chat alpha,
  interlace/format guards, different-device fallback with changing pixels,
  retained-picture lifetime, and GDI handle cleanup.
- Native chat compositor and subpicture/pipe regressions: pixel coverage,
  transparency, clipping, odd widths, cache invalidation, failed allocations,
  resize acknowledgments, reconnects, and outstanding picture ownership.
- Replay color (8), overlay readability (4), window sharing (5), and live first
  seek (17) interactive tests passed. The readability test preserved all 112
  half-covered glyph samples through real VLC resizing.

Initial fallback-color assertions exposed the existing VLC 3 RGB converter's
BT.601 behavior for other color tags. The primary GPU path remains checked
against the source colors; the emergency fallback is checked against independent
equations matching the existing converter. A moving capture fixture also crossed
the capture test's dark-pixel threshold; a constant blue fixture passed without
changing the assertion. The original observations are retained with the task
artifacts.

Exploratory alternatives were rejected when measurements did not support them:
switching to D3D11 increased CPU/memory in the probe and conflicts with the
previously diagnosed replay download issue; sharing devices across players and
removing GDI flushes did not establish a useful rendering-utilization reduction.
Those probes are separate from the final comparisons. No lower source quality,
frame-rate cap, skipped-frame policy, or software-decoding offload is used.
