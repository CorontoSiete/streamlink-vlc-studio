# Saved VOD startup investigation (2026-09-26)

The reported delay was reproduced with the recently reopened Twitch archive
`2875113048` at its saved position, `04:41:26.034`, using the configured Streamlink,
libVLC 3.0.23, muted-segment repair, and the production native chat overlay.
Measurements include fresh URL resolution, player creation, resume confirmation,
and a subsequently displayed picture. Every run also checks the full duration,
absolute position, and continued output.

## Verified change

The direct resolver already downloads and validates the selected media playlist.
The playback gateway downloaded it again immediately to inspect muted segments.
The resolver now passes the validated, absolute-URL playlist to the gateway through
a one-use in-memory handoff. The gateway still inspects the playlist and creates
the same muted-segment repair session.

Only completed playlists qualify. The handoff expires after 15 seconds, retains
at most eight entries and four million UTF-16 characters, and atomically removes
an entry when consumed. Different media URLs/qualities do not share entries.
Oversized, expired, absent, and growing playlists use the existing read path.
Each new resolution still requests playback authorization and validates the current
selected playlist. Custom HTTP clients have isolated handoffs unless explicitly
wired together. No tokens or playlists are persisted.

## Measurements and limits

Logs are in the ignored `artifacts/vod-resume-speed` directory.

| Stage | Before, three trials | After, three trials |
| --- | --- | --- |
| Playlist preparation | 158, 85, 81 ms | 30, 21, 10 ms |
| Resolution through confirmed displayed output | 3584, 3150, 3205 ms | 3947, 3518, 2873 ms |

This removes a measured 64–128 ms of preparation work in these trials. The
end-to-end results overlap and do **not** establish a reduction in the remaining
roughly three-second resume delay. Network and VLC startup variation are larger
than the saving. This change must not be presented as an instant-resume fix.

Native experiments with earlier seek readiness, initial timestamp priming, and
the existing 500 ms network buffer setting did not demonstrate a consistent
end-to-end improvement; they were reverted. The production playback engine,
native pause module, buffering, and exact output confirmation retain their
pre-investigation behavior. VideoLAN's
[adaptive implementation](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/PlaylistManager.cpp)
and [input loop](https://github.com/videolan/vlc/blob/3.0.23/src/input/input.c)
were checked against the experiments, rather than inferring readiness from the
immediate seek-time echo.

## Reproduction

Build the Release test executable. Set `SVS_TEST_VLC_DIRECTORY`,
`SVS_TEST_STREAMLINK_PATH`, `SVS_TEST_STARTUP_VOD_ID=2875113048`,
`SVS_TEST_STARTUP_POSITION=04:41:26.034`, and
`SVS_TEST_FILTER=VOD startup: provider resolution to presented output at bookmark`.
`SVS_TEST_STARTUP_FRESH_PLAYLIST=1` disables the handoff in the test gateway to
measure the original duplicate read; `0` uses production behavior. Each invocation
makes three opens. Run comparisons sequentially without other playback tests.

Offline regressions verify the eliminated HTTP request, retained muted repair,
fresh authorization on reopening, growing playlists, cancellation, expiry,
quality isolation, memory bounds, and a single consumer under contention.

Validation passed: 247 VOD tests with native VLC enabled, 20 Streamlink tests,
and the final 17 startup tests, with no failures or skips. The Release solution
build used warnings as errors; whitespace verification and `git diff --check`
passed. The self-contained Windows x64 executable was published and installed at
`C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe`.

Installed and published SHA-256:
`05BD2A79F886A24B61D1879E6407411903839B628D1E03C95255F46216D8841E`.
The previous executable is preserved in
`artifacts/vod-resume-speed/installed-before.exe`; its hash matches the recorded
pre-install executable. Replacement was atomic and verified after installation.
