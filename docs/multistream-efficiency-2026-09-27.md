# Multistream resource use, 2026-09-27

This change retains the selected video quality, frame rate, latency settings,
audio policy, 40,000-message replay history, native protocol, and 80 ms chat
heartbeat. It removes redundant receiver allocations, makes history eviction
constant-time, bounds tab diagnostic delivery, and gives Streamlink commands
explicit ownership of their descendants.

The baseline is the working tree captured at `2026-09-27T06:10:46.1153877Z`,
including its existing edits, at Git revision
`12e0adbb8476aa3ede6353d1be7701f47761a358`. All 1,163 original files were archived
and verified against the captured SHA-256 manifest. Both application versions
use the current DXVA2 policy. Earlier D3D11 results are not this baseline.
The previously observed parentless Streamlink processes were left alone and
are not counted as savings.

All 36 controlled trials passed, with zero lost video pictures, visible chat
on every pane, and complete cleanup of the observed child processes. The
subsystem reductions below are reproducible; a consistent whole-application
CPU reduction is **not established**. In particular, eight streams with
animated chat used more CPU in all three updated trials. Private memory and
managed allocation rates were essentially unchanged.

Each entry below is the median of three trials. CPU ranges show all three
observations; no completed trial was excluded.

| Streams | Chat | CPU cores before (range) | CPU cores after (range) | Median CPU change | Private MiB before / after |
| --- | --- | --- | --- | --- | --- |
| 4 | quiet | 0.785 (0.619–0.938) | 0.642 (0.620–0.660) | -18.2% | 2246.5 / 2245.8 |
| 4 | busy | 0.773 (0.714–1.925) | 0.748 (0.743–0.995) | -3.2% | 2276.3 / 2270.3 |
| 4 | animated | 0.782 (0.726–1.094) | 0.756 (0.730–1.386) | -3.3% | 2069.3 / 2061.5 |
| 8 | quiet | 1.248 (1.141–1.678) | 1.198 (1.147–1.214) | -4.0% | 4191.3 / 4189.4 |
| 8 | busy | 1.400 (1.330–1.450) | 1.446 (1.358–1.948) | +3.3% | 4223.4 / 4233.6 |
| 8 | animated | 1.533 (1.299–1.632) | 1.948 (1.726–2.304) | +27.1% | 3765.6 / 3765.0 |

Three short trials do not support a precise expected CPU improvement. The
four-stream ranges overlap, and both versions show substantial variability.
The eight-stream animated result is a regression in this measured sample;
separate diagnostic runs below do not replace it or establish its cause.

| Streams | Chat | Working set MiB before / after | GPU decode % before / after | GPU 3D % before / after | Managed MiB/s before / after |
| --- | --- | --- | --- | --- | --- |
| 4 | quiet | 1553.3 / 1556.6 | 43.3 / 42.6 | 10.7 / 10.1 | 0.530 / 0.530 |
| 4 | busy | 1569.4 / 1589.0 | 42.7 / 43.2 | 9.7 / 10.5 | 0.499 / 0.530 |
| 4 | animated | 1565.9 / 1565.7 | 42.9 / 43.2 | 10.9 / 11.2 | 0.500 / 0.499 |
| 8 | quiet | 2729.2 / 2728.3 | 51.8 / 52.1 | 6.1 / 6.1 | 0.635 / 0.636 |
| 8 | busy | 2767.9 / 2786.3 | 51.9 / 52.2 | 6.1 / 6.2 | 0.577 / 0.575 |
| 8 | animated | 2730.1 / 2733.3 | 56.6 / 56.4 | 6.2 / 6.2 | 0.634 / 0.634 |

| Streams | Chat | HLS MiB/s before / after | Chat pipe MiB/s before / after | Process I/O read MiB/s before / after | Process I/O write MiB/s before / after |
| --- | --- | --- | --- | --- | --- |
| 4 | quiet | 5.00 / 5.00 | 16.27 / 16.28 | 16.27 / 16.28 | 16.27 / 16.28 |
| 4 | busy | 5.08 / 5.00 | 23.27 / 23.33 | 23.26 / 23.32 | 23.26 / 23.32 |
| 4 | animated | 5.08 / 5.00 | 16.26 / 16.28 | 16.25 / 16.28 | 16.25 / 16.28 |
| 8 | quiet | 9.96 / 10.13 | 32.50 / 32.50 | 32.50 / 32.50 | 32.50 / 32.50 |
| 8 | busy | 10.38 / 10.21 | 48.12 / 48.12 | 48.09 / 48.09 | 48.07 / 48.09 |
| 8 | animated | 9.96 / 10.05 | 32.45 / 32.45 | 32.40 / 32.43 | 32.40 / 32.43 |

Every stream sustained at least 59.53 displayed fps over its sample.
The process tree stayed at 21 processes for four streams and 41 for eight,
including the test host, transport descendants, and native chat helpers.
Process I/O includes pipes and other device operations as reported by Windows;
it is not physical disk traffic. HLS rates vary with segment boundaries.
The protocol still transmits the full frame: receive allocation savings do
not reduce pipe traffic. Per-stream audio/video counters, GPU samples, and
all individual memory/CPU observations are in the raw JSON.

Four additional eight-stream animated runs used ten seconds of warmup and
twenty seconds of measurement. Updated code used 1.364 CPU cores without
sampling, then 1.359 with native thread sampling; the sampled baseline used
1.357. A separately rebuilt control using the updated managed source and the
original bundled overlay DLL used 1.800. That control also embeds the original
DLL hash, so the production capability check still selects DXVA2. None of these
short diagnostic runs replaces a formal trial.

The eight hottest threads were VLC video-output threads, with samples in
swscale, DXVA2 readback, GDI presentation, and VLC scheduling. The control with
the original overlay had two especially busy video-output threads. These
observations show variability and do not isolate a causal regression or justify
an overall CPU-saving claim. Diagnosing the eight-stream animated result remains
an explicit limitation of this comparison.

The application workload uses the production WPF window/view models, real
Streamlink HTTP transports, libVLC, the bundled plugin, and the production
native chat render loop. Each video is moving H.264 at 1920x1080 and 60 fps,
served through a local advancing HLS playlist with two-second segments and
silent AAC. The common chat helper has a 4,000-entry catalog and a 340x292
panel. Quiet chat repeats identical frames; busy chat appends a message every
80 ms; animated chat uses a real GIF with 100/70/90 ms frame durations. The
render thread retains its original heartbeat and animation scheduling.

There are three alternating before/after pairs for each combination of four
or eight streams and quiet, busy, or animated chat. Each fresh process has ten
seconds of warmup and thirty seconds of measurement. Builds and other tests
are stopped during the timed runs. Screenshots are taken after measurement.

CPU seconds, private bytes, working sets, I/O counters, and process counts
cover only the harness and its descendants. CPU cores means CPU seconds
divided by elapsed seconds. WMI reports 24 machine logical CPUs; the test runtime
reports 16 available logical CPUs in its process context. Results use CPU cores
without normalizing to either count.
The diagnostic hosts inherited affinity mask `FFFF00` (logical CPUs 8–23).
Windows reported efficiency class 0 for those CPUs and class 1 for CPUs 0–7.
This desktop execution context limits extrapolation to a manually launched app
with a different affinity; neither benchmark version changes that policy.
GPU values sum this process tree's PDH engine instances by engine type, so
they are not a whole-desktop Task Manager reading. Managed allocations are
the host's `GC.GetTotalAllocatedBytes` delta. HLS HTTP bytes and native pipe
bytes are counted directly. Native allocation counts are measured separately
in the receiver experiment below.

Every trial also captures the composed window and checks for contrasting chat
glyphs in every video pane after measurement. A positive pipe frame counter
alone cannot establish that chat reached the final output. The summarizer
rejects missing chat, lost pictures, low cadence, changed binary hashes, and
process churn during a sample rather than silently dropping a failed run.

A preliminary matrix completed its 18 four-stream runs, then stopped before
the first eight-stream sample because the counter harness requested an all-access
process handle. The corrected harness retains query-only handles and compares
process creation times when following parent PIDs. It reads CPU, memory, and
I/O through the native query APIs. This also removes costly repeated managed
process lookups, which had stretched a diagnostic three-second eight-stream
sample past five seconds and delayed the UI. Microsoft's
[I/O counter API requires only query access](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters).
The complete comparison was restarted with the same corrected harness for both
versions and both stream counts. Production assembly hashes remained unchanged;
the preliminary data, harness revisions, and failed attempt are archived.

These totals include the local HLS server, measurement code, and chat helpers.
Summed working sets count shared pages in each process. The synthetic chat
workload isolates rendering from provider network variability; separate live
smoke tests exercise real Twitch and Kick inputs. The machine has an Intel
Core i9-13900K, 24 reported logical CPUs, 64 GiB RAM, an RTX 4080 with driver
32.0.16.1656, Windows build 19044, .NET SDK 10.0.302, VLC 3.0.23, and Streamlink
8.5.0. The HLS fixture used FFmpeg n8.1.2-20260623. `external-tools.json` records
the installed executable and relevant VLC plugin hashes.

The isolated receiver measurements used 100 frames per stream after warmup.
All three alternating pairs produced the same counts and matching final pixel
hashes before and after:

| Streams | Frames | Receive allocations before / after | Picture allocations before / after | Picture bytes allocated before / after |
| --- | --- | --- | --- | --- |
| 4 | identical | 400 / 0 | 400 / 0 | 197,836,800 / 0 |
| 8 | identical | 800 / 0 | 800 / 0 | 395,673,600 / 0 |
| 4 | changing | 400 / 0 | 400 / 400 | 197,836,800 / 197,836,800 |
| 8 | changing | 800 / 0 | 800 / 800 | 395,673,600 / 395,673,600 |

For four full histories receiving 80,000 messages in total, median elapsed
time fell from 763.9 ms to 24.3 ms (96.8% less). Median CPU time fell from
765.6 ms to 46.9 ms (93.9% less). Managed allocation volume stayed at about
4.88 MiB. These savings apply to eviction work; they are not estimates of
total playback savings.

The native receive test runs the production pipe worker over real named pipes
and uses VLC's real picture and subpicture reference-counting APIs. Before and
after receive exactly the same visible pixel bytes. Its changing-frame case
changes one pixel each iteration; actual GIF timing is covered by the native
render regressions and the application workload. Its CPU counters are too
coarse for percentage claims about very short amounts of processing. Allocation
volume is not retained private memory or working set.

The timeline comparison holds four histories at the original 40,000-message
capacity and appends 20,000 messages to each, in three alternating trials.
The original list implementation is frozen in `OriginalVodChatTimeline.cs`.
Randomized equivalence tests separately exercise append, late insertion,
equal timestamps, duplicate IDs and fallback keys, wrapping, eviction, cursor
movement, seek boundaries, and clear. Eviction and clear release references
immediately. Tab diagnostic tests verify that 10,000 incoming lines queue one
delivery and retain the final 300 lines, with yielding, retry, and disposal
checks. File logging still receives every transport line.

Each redirected command gets a non-inheritable Windows job with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. `PROC_THREAD_ATTRIBUTE_JOB_LIST` assigns
the job as part of `CreateProcessW`; the process is initially suspended only
to adopt its handle safely before resuming it. An explicit handle list limits
inherited standard handles. Closing the job terminates descendants even after
the launcher exits. The implementation follows Microsoft's
[job ownership semantics](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
and [atomic job assignment mechanism](https://devblogs.microsoft.com/oldnewthing/20230209-00/?p=107812).

The plugin retains a worker-owned receive buffer bounded by the existing
32 MiB payload limit. It compares dimensions, alpha, and visible bytes row by
row against an immutable referenced frame. Equal frames retain their picture
identity and cached overlay regions; changed frames allocate a new picture.
Existing subpictures keep their own references. Resize acknowledgment and
other protocol state still advance on identical frames.

The bundled DLL was rebuilt with MSVCRT GCC 16.1.0 and VLC 3.0.23 headers.
`dependencies/native-overlay.json` records the updated size and SHA-256.

An initial application run crashed during concurrent stock VLC initialization,
before the overlay plugin loaded or measurement started. The original logs
are retained. A separate C program reproduced the failure using only the
installed VLC libraries and concurrent environment updates; serialized startup
passed 100 fresh-process trials. The application now uses one short shared
gate for `libvlc_new` and MSVCRT environment updates. Playback remains
concurrent. Both measurement versions serialize engine creation before
warmup, keeping the baseline production binaries unchanged and startup work
outside the comparison.

The final plugin search path is assembled before it is published, and matching
managed/native environment values are left alone. This avoids repeatedly
reallocating MSVCRT's process-wide environment while earlier players run.
Microsoft documents that [environment reads and writes require synchronization](https://learn.microsoft.com/en-us/cpp/c-runtime-library/reference/putenv-s-wputenv-s?view=msvc-170).
The eight-player initialization regression passed ten fresh-process runs before
the final desktop and application validations.

An intermediate four-stream quiet-chat comparison was stopped after four
completed trials because the updated host used substantially more CPU and one
capture lacked chat in two panes. Those raw trials and the interrupted trial's
cleanup record are preserved. This prompted the environment-write changes and
the per-pane visibility assertion before starting a fresh complete matrix.
Separate short diagnostic runs then showed CPU spikes in both versions; they
do not establish a cause. Some used verbose native logging or stack sampling.
These attempts are documented separately and are not pooled into the final
ten-second warmup / thirty-second measurement results.

| Binary | Before | After |
| --- | --- | --- |
| `libmyoverlay_plugin.dll` bytes | 971,165 | 970,959 |
| Plugin SHA-256 | `41354c6ad4f43e5a31e1c4d4a945791fb3ce914c62c0588de1d1559d51d0fd1a` | `f00754305b56235581a5dc3f978a07c3690b93615d4b12c37e8c57a12224d314` |
| `vlc_chat_overlay.exe` bytes | 412,952 | 412,952 |
| Controller SHA-256 | `26dee8d3ab1a1f1a36b69f61b6a7c8d092002ed8522cd744c964f93da3dc677d` | unchanged |

`scripts/dev.ps1 Check` passed its locked restore, formatting verification,
PowerShell/tooling checks, native dependency hashes, Release build with zero
warnings/errors, and all 1,293 managed tests. Its existing 252 interactive
tests were skipped in that headless configuration. Seven selected desktop
groups then passed 95 test executions with zero skips: native replay overlays,
readability, window capture/rebinding, replay pixels, audio and native playback,
start/hide/resume/stop/restart, and prepared replay adoption.

Native TLS/network/rendering, subpicture/receiver, and compositor suites also
passed. Coverage includes exact cached/fresh pixel comparisons, GIF deadlines,
resize, clear, reconnect, malformed/partial input, failed allocations, cached
region identity, held subpictures after close, and stable GDI handle counts.
Nine process-ownership tests passed launcher exit, stop, cancellation, timeout,
abrupt owner exit, failed startup, handle cleanup, argument/environment/encoding
fidelity, independent sessions, and survival of an unrelated process.

The mixed live smoke test completed two open/play/chat/close cycles with
Twitch `eslcs` and `esl_dota2`, plus Kick `cuffem` and `deenthegreat`. Two more
cycles used Twitch `valorant` and `eslcs` with the same Kick channels, to observe
active Twitch user chat. Streamlink probes offered 1080p60 on all selected
channels. Real provider chat clients and native controllers were used, with
composed-window captures retained for each cycle.

| Live set / cycle | Displayed pictures in the requested 20-second interval, in tab order | Lost pictures | Chat entries, including status messages |
| --- | --- | --- | --- |
| Initial / 1 | 1199, 1207, 1201, 1201 | 0, 0, 0, 0 | 3, 3, 100, 44 |
| Initial / 2 | 1196, 1207, 1123, 1125 | 0, 0, 5, 0 | 3, 3, 100, 35 |
| Active Twitch chat / 1 | 1209, 1196, 1192, 1198 | 0, 0, 0, 0 | 14, 4, 100, 26 |
| Active Twitch chat / 2 | 1189, 1203, 1206, 1188 | 0, 0, 0, 0 | 17, 3, 100, 34 |

All four streams kept advancing, their native controllers remained current,
only the selected fourth tab was audible, and every observed child process
exited after each close cycle. The first Twitch channels were quiet during
the short capture; the additional `valorant` runs visibly received user
messages. Kick chat and emotes were visible in both sets. The live smoke's
advancement and cleanup assertions passed, but it is **not** a zero-loss result:
`cuffem` lost five pictures during the second initial cycle. The controlled
36-trial comparison had zero lost video pictures and zero lost audio buffers.
Live provider traffic is not a controlled before/after efficiency comparison.

The [reproduction instructions](measurements/multistream-2026-09-27/REPRODUCE.md)
record the tool paths, HLS construction, test environment, native instrumentation,
and alternating run commands. The [application summary](measurements/multistream-2026-09-27/application-summary.json)
contains each of the 36 trials plus medians; the separate
[receiver](measurements/multistream-2026-09-27/receiver.json) and
[timeline](measurements/multistream-2026-09-27/timeline.json) files retain the
subsystem observations.

The [raw results archive](measurements/multistream-2026-09-27/raw-results.zip)
includes individual samples, screenshots, test logs, manifests, earlier failed
attempts, and diagnostic profiles. `application-verified` is the completed
36-run matrix. The [baseline source archive](measurements/multistream-2026-09-27/baseline-sources.zip)
preserves every file in the original dirty working tree; apply the
[common measurement harness](measurements/multistream-2026-09-27/baseline-harness.zip)
to reproduce the baseline measurements. Earlier harness revisions remain in
the same directory for auditing the counter correction.

Each archive has a SHA-256 and size in
[archives.json](measurements/multistream-2026-09-27/archives.json), and the raw
archive contains a per-file hash manifest. The
[final verification](measurements/multistream-2026-09-27/final-verification.json)
checks the original archive and confirms that the measured application,
infrastructure, core, test, and bundled native binaries still match after the
final repository check. Later edits affect this report and summary validation,
not the measured production code. The large input movie and HLS segments remain
in the workspace at the paths documented in the reproduction instructions.
