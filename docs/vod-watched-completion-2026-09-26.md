# VOD watched status at decoder EOF

## Reproduction

The finished screen and playback history used different completion rules. The
screen accepted the current VLC input's `Ended` state. History additionally
required a previous accepted bookmark within five seconds of its duration and
no outstanding seek confirmation. A seek into the last frame can reach EOF
before another clock sample, so VLC could finish the screen while leaving no
completed bookmark. A missed final clock sample caused the same disagreement.

The local history contained an unwatched bookmark for `2881171681` at
`14:03:06.434` of `14:03:13.316`, outside the five-second window. That saved
record alone does not establish that the earlier playback reached EOF.

Adding watched-history assertions to the existing real VLC completion tests
reproduced eight failures out of nine MP4/HLS scenarios before the fix. The
decoder had reached EOF and the screen had finished, but history was absent or
incomplete. The failures included exact-end seeks, final-frame seeks, and
reopening a finished input at its end. See
`artifacts/vod-watched-fix/native-before.log`.

Eleven deterministic regressions cover the history/card/save paths and sample
ordering; seven failed before the fix. Besides the missing-bookmark cases,
they reproduced accepting a stale EOF when the seek generation changed during
health sampling. See `regressions-before.log` in the same artifact directory.

## Change

The current input's validated EOF now records a completed, watched bookmark
using its known duration. Completion does not require an intermediate resume
sample or a seek-confirmation clock after EOF. Duration comes from the decoder,
the last accepted bookmark, the restored bookmark, or the VOD metadata.

The finished-screen path passes its already validated EOF observation to the
history path. It no longer depends on a second nonblocking decoder health read
succeeding. Engine identity, seek generation, playback-state generation, and
the closed-history guard protect against outdated observations. Completion
queues an immediate save through the existing serialized atomic history writer;
checkpoint and close saves retain the existing retry behavior.

Only decoder EOF establishes completion. A near-end position, paused player,
buffering, stopped input, or decoder error does not. The previous resume test
that treated an early `Ended` sample as an error now uses the actual `Error`
state; EOF follows the same semantics as the finished screen.

The tests verify existing Twitch/Kick cards update, completion reaches disk,
reopening starts from the beginning, and a newer seek/restart/stop rejects old
samples. Real VLC tests also read the saved history through a new history
instance. These reads wait behind the already queued save, without requesting
an extra write or locking the destination against atomic replacement.

The existing user history is not retroactively reclassified: a near-end saved
position alone is insufficient evidence of EOF.

## Validation and installed build

The eleven focused regressions passed. The broader Release run with
`SVS_TEST_FILTER=VOD` and native VLC enabled passed **271 tests, zero failures,
timeouts, or skips**. This includes all nine MP4/HLS completion scenarios,
two long-timeline completion scenarios (including muted-segment repair), saved
resume positions, and the watched-badge rendering tests in both themes.
The complete output is `artifacts/vod-watched-fix/vod-suite.log`.

The Release solution build with warnings treated as errors completed with zero
warnings and errors. The self-contained Windows x64 single-file publish,
`git diff --check`, and whitespace checks for the new files also passed.

The executable was installed atomically at
`C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe` after checking
that the app was closed and its previous executable had not changed. The
installed and published SHA-256 is:

`AAF6FA7E0A24BF04F4D77562E00DA341963787010EC296B6B05451964B7E4BD9`

The previous executable is retained in
`artifacts/vod-watched-fix/installed-before.exe`, with verified SHA-256:

`A0A7985DBA209FAC9624F17877E0926576D9D3A1E18E2C052FD006AA264E7D93`
