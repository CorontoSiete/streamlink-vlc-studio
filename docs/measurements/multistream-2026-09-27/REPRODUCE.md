# Reproducing the multistream measurements

Run on an interactive Windows desktop. These measurements used .NET SDK
10.0.302, VLC 3.0.23, Streamlink 8.5.0, FFmpeg n8.1.2-20260623, and MSVCRT MinGW
GCC 16.1.0. The exact source,
assembly, native binary, media, and machine manifests accompany the raw data.
Keep the same machine, DXVA2 policy, display geometry, and inputs for both
versions. Do not run builds or other playback tests during timed trials.
The measured runtime reported 16 available logical CPUs. Diagnostic hosts
inherited affinity mask `FFFF00`, covering logical CPUs 8–23 (Windows efficiency
class 0); the other eight CPUs reported class 1. CPU results are unnormalized
CPU seconds divided by elapsed seconds. A different affinity is a different
execution context and should be recorded, not silently compared to these data.

`baseline-sources.zip` is the original dirty working tree, before this change.
Every archived file matches `baseline-manifest.json`. Extract it into a fresh
directory. Then extract `baseline-harness.zip` over it: this adds only the common
measurement harness and its test registration, plus the instrumented native
receiver test. The baseline production source and bundled plugin stay intact.

From the updated repository root, set paths to the extracted baseline and tools:

```powershell
$before = (Resolve-Path '.tmp/multistream-baseline').Path
$dotnet = 'C:/Users/ComputerGuy/.dotnet/dotnet.exe'
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$gcc = '.tools/native-review-msvcrt/mingw64/bin/gcc.exe'
$vlc = 'C:/Program Files/VideoLAN/VLC'
$include = 'C:/Users/ComputerGuy/Downloads/vlc-overlay/vlc-3.0.23/include'
$library = 'C:/Users/ComputerGuy/Downloads/vlc-overlay/sdk'
$evidence = '.tmp/multistream-reproduction'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
& $dotnet restore "$before/StreamlinkVlcStudio.sln" --locked-mode
& $dotnet build "$before/tests/StreamlinkVlcStudio.Tests/StreamlinkVlcStudio.Tests.csproj" -c Release --no-restore
& ./scripts/dev.ps1 Check
& ./scripts/test-chat-render-resources.ps1 -Gcc $gcc -OutputDirectory "$evidence/native-workload"
```

The measured input movie remains at `.tmp/multistream-resources/1080p60-long.mp4`
in the original workspace (341,248,153 bytes; SHA-256
`7f81266e9aa1681014d8f65f71f91ac62c31fa098a3bb68aae77132b5f48b85b`).
It is a moving 1080p60 test pattern. The large movie and generated HLS segments
are not duplicated in the small evidence archive. `measurement-inputs.json`
records the hashes of every segment. The conversion used:

```powershell
New-Item -ItemType Directory -Force -Path "$evidence/hls" | Out-Null
& 'C:/Program Files/Streamlink/ffmpeg/ffmpeg.exe' -nostdin -hide_banner -loglevel error -y `
  -i '.tmp/multistream-resources/1080p60-long.mp4' -f lavfi -i 'anullsrc=r=48000:cl=stereo' `
  -t 120 -map '0:v' -map '1:a' -c:v libx264 -preset veryfast -crf 20 -threads 6 `
  -g 120 -keyint_min 120 -sc_threshold 0 -pix_fmt yuv420p -c:a aac -b:a 32k `
  -hls_time 2 -hls_list_size 0 -hls_playlist_type event `
  -hls_segment_filename "$evidence/hls/segment%06d.ts" "$evidence/hls/index.m3u8"
```

Use the same segment files for before and after. The fixture server advances
the playlist every two seconds; it does not replay the static event playlist.
The harness creates four or eight real WPF tabs with real Streamlink transports,
VLC players, and native chat helpers. Its provider chat clients are deterministic
fixtures. Each process verifies cleanup of all descendants it observed.

```powershell
& ./scripts/measure-multistream.ps1 `
  -BaselineTests "$before/tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll" `
  -UpdatedTests 'tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll' `
  -HlsDirectory "$evidence/hls" -NativeRenderer "$evidence/native-workload/render-resources.exe" `
  -OutputDirectory "$evidence/application" -DotNetPath $dotnet -Streams @(8, 4)
python scripts/summarize-multistream.py "$evidence/application"
```

Defaults are all six workloads, three alternating pairs, ten seconds warmup,
and thirty seconds measurement: 36 fresh processes. Use a fresh output directory;
the runner refuses to overwrite raw trials. `-StopFile path` stops between trials
if that file appears. `-VlcDiagnostics` is for separate investigations only: verbose
logging allocates and writes files, so never enable it for performance comparisons.
Each trial emits JSON, a composed-window PNG, per-pane chat visibility counts,
test output, and application logs. `summary.json` retains every trial as well as
medians. A behavior failure is a failed trial, not a reason to discard its numbers.

To reproduce the receiver comparison, compile the same instrumentation against
each production receiver. For the baseline use `-DBASELINE_RECEIVE`; it selects
the original malloc counter and omits assertions that refer to the new buffer.
The baseline harness archive places `received-frames.c` alongside its original
`myoverlay.c` include target.

```powershell
New-Item -ItemType Directory -Force -Path "$evidence/native-before" | Out-Null
& $gcc -O2 -Wall -Wextra -Werror -static-libgcc -D__PLUGIN__ -DWIN32 -D_WIN32_WINNT=0x0601 `
  -DBASELINE_RECEIVE -include "$before/native/chat-overlay/quality-gdi/build-config.h" `
  -I $include -L $library "$before/native/chat-overlay/tests/received-frames.c" `
  -o "$evidence/native-before/received-frames.exe" -lvlccore -luser32
& ./scripts/test-chat-subpictures.ps1 -Gcc $gcc -VlcIncludeDirectory $include `
  -VlcLibraryDirectory $library -VlcDirectory $vlc -OutputDirectory "$evidence/native-after"
$env:PATH = "$vlc;" + $env:PATH
& "$evidence/native-before/received-frames.exe" --benchmark quiet 4 100
& "$evidence/native-after/received-frames.exe" --benchmark quiet 4 100
```

Repeat with `busy` and eight streams, alternating the order for three pairs.
`run-subsystems.ps1` in the raw archive records the exact original orchestration.
The timeline benchmark similarly runs three alternating pairs against a frozen
copy of the original implementation within the same executable:

```powershell
$env:SVS_BENCHMARK_TIMELINE = '1'
$env:SVS_BENCHMARK_TIMELINE_OUTPUT = "$evidence/timeline.json"
$env:SVS_TEST_FILTER = 'multistream resources: four full history benchmark'
& $dotnet 'tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll'
```

The archive contains the full repository check log, selected desktop suite logs,
native receiver/render/compositor logs, process ownership checks, initialization
repetitions, and the separate mixed-provider live smoke results. Live stream
availability is time dependent. Set `SVS_TEST_MULTISTREAM_LIVE_URLS` to four live
public Twitch/Kick URLs, `SVS_TEST_MULTISTREAM_LIVE_OUTPUT` to a fresh directory,
and `SVS_TEST_FILTER` to `multistream live smoke:` to repeat its two open/play/chat/
close cycles. No chat messages are sent.

Earlier diagnostic attempts are retained separately from the completed matrix.
They include the native startup failure, the interrupted CPU investigation, and
short runs with profiling or verbose logging. They are not pooled into the final
10/30-second comparison. Memory dumps containing process memory are excluded.

The final 36-run matrix is in `application-verified`. The preliminary
`application-complete` directory has 18 successful four-stream trials and a failed
eight-stream setup. The initial counter implementation requested an all-access
process handle and repeated expensive managed process lookups. The final harness
uses retained query-only handles, parent/child creation times, and native CPU,
memory, and I/O queries. The complete comparison was restarted with this common
harness; production assembly hashes stayed unchanged. Earlier harness archives
and their corresponding source/binary manifests preserve the diagnostic revisions.
