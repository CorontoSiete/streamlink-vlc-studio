# PiP right-click input

The original mouse hook waited 25 ms for the WPF dispatcher and aborted a
pending right-button operation when that deadline elapsed. During playback,
Windows delivers the click to VLC's renderer child HWND, which does not raise
the managed video surface's fallback event. The top-bar menu therefore never
opened when the dispatcher was briefly busy.

Before the fix, the new physical-input regressions failed with a 300 ms UI stall
over a native child HWND and over actual Direct3D11 and GDI VLC renderers. All
three failures were a missing menu after the dispatcher resumed. The tests use
the production mouse hook and confirm the native HWND under the cursor before
injecting input.

The hook now captures the registered PiP owner and original screen coordinates,
queues its menu callback, and immediately consumes the press and matching
release. The callback has no synchronous deadline. Consuming the release also
prevents WPF from dismissing a menu whose intercepted press never reached its
mouse device. The PiP activates when it opens the menu.

Native ownership includes VLC and replay-seekbar descendants. The window's
active tab supplies the preference when the click lands on a border or empty
multiview cell. Existing context-menu popups and other applications retain their
own input. The volume indicator explicitly aliases its PiP registration, so a
captured click survives the indicator's higher-priority hide timer. Closing or
replacing the PiP invalidates retained callbacks.

Regression coverage includes:

- Physical menu opening and top-bar selection with native child, Direct3D11,
  and GDI targets, active and inactive windows, delayed dispatch, pointer
  movement, and repeated show/hide cycles. Each click must open one persistent
  menu and produce one preference change.
- Replay seekbar, resize border, empty multiview cell, volume-indicator expiry,
  and an already-open menu popup.
- Ordered dispatcher delivery, slow callbacks, matching releases, shutdown,
  native ownership, disabled/foreign windows, and stale registrations.
- Existing PiP sizing, dragging, fullscreen, activation, and wheel routing.
- A separate foreground process, entered with physical mouse input, followed by
  idle and delayed PiP right clicks and physical menu selection. PiP must gain
  foreground activation without the test forcing it after the right click.

Build with the pinned .NET 10.0.302 SDK. The focused test filters are
`picture-in-picture`, `PiP context`, `wheel`, and `low-level mouse hook` via
`SVS_TEST_FILTER`. For real VLC tests, set `SVS_TEST_VLC_DIRECTORY` to a directory
containing `libvlc.dll` and `SVS_TEST_VLC_MEDIA` to a local video fixture. Set
`SVS_EXPECTED_MAX_SKIPS=0` on an interactive Windows desktop.

Reproduction, build, format, and test output from this verification are retained
in `.tmp/pip-context-menu-verification`.

Final Windows verification passed with no skipped tests: 51 tests in the PiP
filter, 11 dispatcher/ownership tests, 17 wheel tests, 3 low-level hook tests,
and the additional foreign-foreground test. The Release solution build completed
with zero warnings or errors; changed-file whitespace verification passed.
