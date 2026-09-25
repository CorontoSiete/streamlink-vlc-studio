# Home workflow and resource usage

This pass reduces repeated Home requests and collection churn, and improves
Recent loading with bounded concurrency.

- Enter joins a running automatic search for the same query and quality. After
  completion, Enter still refreshes. Editing the query cancels the old work;
  generation checks reject late responses even from a provider that ignores
  cancellation. Changing quality starts a new request.
- Viewer-count enrichment updates existing search row objects and moves them
  into their new order. It preserves each row's commands and sends property
  notifications for the count and its display text, without clearing the list.
- Recent retains successful live/offline metadata freshness for the existing
  refresh interval (five minutes by default). Revisiting the page avoids another
  lookup and card rebuild while results are fresh. New channels and failed
  lookups remain eligible immediately. The periodic timer still refreshes on its
  normal cadence while Recent is visible. Freshness is transient and is removed
  when a recent channel is deleted.
- Recent uses at most four workers, rather than checking channels serially or
  creating a waiting task for every channel. Duplicate channel identities share
  one lookup. Shutdown cancels active checks and prevents queued channels from
  starting; results for deleted channels are not reapplied.
- The CI skip ceiling now includes the pre-existing hidden-emote visibility
  test, which requires a desktop. The ceiling moves from 207 to 208; the ten new
  workflow regressions all run without a visible desktop.

## Evidence

The regression fixture with two Recent channels and six page visits made 12
metadata requests before this change and two afterward. Unchanged revisit
results now trigger zero collection changes. Enter during an automatic search
made two discovery requests before and one afterward. Viewer-count updates
preserve row and command identity and emit collection moves instead of resets.

The controlled nine-channel fixture admits four requests simultaneously, never
exceeds four, and looks up each distinct channel once. Shutdown with the first
four requests held open cancels them without starting the remaining five.

Seven of the first eight new regression checks failed against the original
application binaries; the existing retry behavior already passed. Additional
checks cover quality changes and freshness expiry, clock rollback, and deletion.
These measurements concern request counts, concurrency, and UI object reuse;
they do not establish a percentage reduction in total playback CPU or memory.

## Validation

- Release solution build with warnings treated as errors: zero warnings/errors.
- All ten new regressions pass without a visible desktop, including after
  changing Recent to use a fixed worker pool.
- Full suite through `dotnet test --no-build --no-restore`: 756 passed, 208
  desktop-only skips, zero failures/timeouts; the 208-skip ceiling was enforced.
- Formatting verification for all changed C# files: passed.
- Locked runtime restore with transitive vulnerability auditing and single-file
  self-contained publish with warnings treated as errors: passed.

The first full run's two packaging failures were caused by subprocesses finding
the older system SDK. Prepending the installed 10.0.302 SDK directory to PATH
resolved both; the complete final suite passed with the pinned SDK. Interactive
desktop/input behavior and authenticated live-provider requests were not tested
in this pass.

## Local artifacts

The pre-edit files are in `.tmp/workflow-resource-before/`; the isolated diff is
`.tmp/workflow-resource.patch`. Build, formatting, reproduction, and test logs
use the `.tmp/workflow-resource-` prefix.
The updated executable is `artifacts/workflow-resources/StreamStudio.exe`.
