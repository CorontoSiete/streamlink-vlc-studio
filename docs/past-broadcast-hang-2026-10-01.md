# Past broadcasts search hang — 2026-10-01

The investigation reproduced a UI hang in the native chat overlay's keyboard-focus
handoff. Focusing the actual Past broadcasts streamer input synchronously waited
on each open stream's event pipe. Chat reconfiguration invoked the same blocking
operation on the UI thread. Those operations are now asynchronous, with a deadline
covering both connection and writing.

The original window disappearance cannot be attributed conclusively to this hang:
Windows recorded an application hang, but there is no dump or exception stack for
that original process. The reproduced and fixed hang is supported by captured
stacks and failing-before/passing-after regressions.

## Original incident

The user reported Twitch selected, `xqc` entered in the streamer input beside the
Twitch/Kick buttons, and the window disappearing when results appeared. The same
search later worked. The user did not remember whether streams had just been
opened before returning Home.

Windows Application Hang event 1002 at **03:00:21 EDT** identifies PID **17708**,
started at **02:59:06**, running this workspace's Release `StreamStudio.exe`.
The application log ends abruptly at **02:59:50.675**, during a replay probe after
`babyboots17` started playing. Six streams had been opened: `summit1g`, `amouranth`,
`naughty`, `albralelie`, `jennah`, and `babyboots17`.

There is no matching .NET Runtime/Application Error exception or available hang
dump for this incident. The earlier 02:23/02:24 `Run`/`VisualTreeHelper` crashes
are a separate incident already addressed in
[the tab-click diagnosis](tab-switch-crash-2026-10-01.md).

Evidence: [Windows hang event](../.artifacts/past-broadcast-crash/windows-hang-events.xml),
[original build hashes](../.artifacts/past-broadcast-crash/baseline-build-hashes.json).
The original application DLL SHA-256 was
`1CD2B6BF817FE87A16F352C1D2F703F2AD81AE064B6218902502924FE4D80CB5`.

## Captured blocking path

The unchanged application DLL was exercised with real Twitch/Streamlink/VLC
services, real native overlays, a playing hover preview, and six streams grouped
in multiview. `babyboots17` was offline by then, so the sixth stream was the
currently live `ironmouse`; the other five channels matched the original log.
The diagnostic runner used the actual MainWindow, bindings, debounce, and VOD
service. Its startup mode omitted only App's single-instance signaling.

PID **19784**, UI OS thread **18268**, was captured while unresponsive:

```text
MainWindowPreviewGotKeyboardFocus
  ReleaseNativeOverlayChatInputFocusForWpfTextInput
    MainViewModel.ReleaseNativeOverlayChatInputFocus
      StreamTabViewModel.TryReleaseNativeOverlayChatInputFocus
        NativeChatOverlayController.TryReleaseNativeOverlayChatInputFocus
          TryWriteNativeOverlayEventSynchronously
            NamedPipeClientStream.Connect / SpinWait / Thread.Sleep
```

A second capture shows `StopNativeOverlayChatAsync` invoking the same synchronous
release inside playback/chat reconfiguration. A 20-second profile contains
**2,305 of 9,119 UI samples** in the synchronous focus-release helper.

The helper allowed up to 250 ms per unavailable pipe. MainViewModel called it
serially for every tab; six unavailable overlays blocked the real search input
for **1,524 ms** in the regression. Its synchronous `Write` also lacked a complete
operation deadline: a connected, deliberately nonreading test pipe kept a large
write pending beyond 600 ms despite the 250 ms timeout. That transport test does
not establish that the original incident stalled during a write.

Evidence: [focus stack](../.artifacts/past-broadcast-crash/user-live-19784-20261001-033003-734.log),
[reconfiguration stack](../.artifacts/past-broadcast-crash/user-live-19784-20261001-033006-534.log),
[baseline UI profile](../.artifacts/past-broadcast-crash/ui-profile-19784-summary.log),
[three failing baseline regressions](../.artifacts/past-broadcast-crash/baseline-regressions.log).

## Change and verification

The event pipe now uses `PipeOptions.Asynchronous`, `ConnectAsync`, and
`WriteAsync`, with one cancellation deadline for the entire 250 ms operation.
Continuations run without requiring the WPF dispatcher. MainViewModel tracks
requests without waiting in input handlers; shutdown awaits them asynchronously.
Repeated focus/mouse requests share a pending release per pipe, while a new
player's pipe gets its own operation. Disposal drains pending releases and
rejects new input requests. The detached-controller fallback and native
focus-release packet are preserved.

The [.NET pipe options documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0)
and [.NET 10.0.7 connection implementation](https://github.com/dotnet/runtime/blob/v10.0.7/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeClientStream.cs)
support the transport behavior checked here.

All six focused regressions pass: real search-input responsiveness with six
unavailable overlays, a bounded nonreading-pipe write, yielding shutdown, delivery
to a late pipe, coalescing across player changes, and disposal. The existing real
native keyboard-capture test also passes with zero skips, retaining every typed
character in docked chat, Theatre, and the detached-controller interval.
The six regressions also pass on the installed runtime used by the application.
The complete `scripts/dev.ps1 Check -NoRestore` succeeds: formatting, PowerShell
tooling contracts, native dependency verification, a Release build with zero
warnings/errors, and **1,476 passed tests / 269 skipped desktop tests**.

The fixed live startup/multiview run completed nine `xqc` searches with **50 real
results each**, then exited normally. Its UI profile contains **9,296 samples**
and **zero** synchronous pipe-connect, sleep, or spin-wait frames. Some rendering
delays remain: a captured delay is inside WPF composition, and the background
heartbeat reached approximately three seconds during repeated page changes.
This investigation does not claim that every rendering delay is removed or that
the original process disappearance has been conclusively explained.

The tested application DLL SHA-256 is
`C9979F0A7FCE66ED1705B12750DB85B44BAD1AAC8DFCC96505B3C884DD7ADA02`.

Evidence: [focused regressions](../.artifacts/past-broadcast-crash/fixed-regressions.log),
[installed-runtime regressions](../.artifacts/past-broadcast-crash/fixed-installed-runtime-regressions.log),
[real keyboard test](../.artifacts/past-broadcast-crash/fixed-native-keyboard.log),
[fixed live run](../.artifacts/past-broadcast-crash/fixed-production-grid-profile-stress-verified.log),
[fixed UI profile](../.artifacts/past-broadcast-crash/ui-profile-29156-summary.log),
[rendering delay stack](../.artifacts/past-broadcast-crash/user-live-29156-20261001-034524-012.log),
[complete repository check](../.artifacts/past-broadcast-crash/fixed-complete-check.log).

The first attempted fixed live run received the wrong diagnostic output-directory
argument and exited before issuing the search. That harness error is preserved
in `fixed-production-grid-profile-stress.log`; the verified run above used the
correct invocation.

Computer Use subsequently reported that it had been stopped with the physical
Escape key. UI automation stopped; the rebuilt user application was not reopened.
