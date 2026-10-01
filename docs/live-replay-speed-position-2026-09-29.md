# Live Twitch replay position after playback speed changes (2026-09-29)

## Reproduction

The native engine was tested against the growing archive of a live Lirik Twitch
broadcast (`2887275691`) and a generated 60-second MPEG-TS EVENT playlist. Each
test started with live playback, adopted a prepared replay input, confirmed video
at a seeked position, and changed speed repeatedly on the same player.

Before the correction, the local replay reported 16.707 seconds and then zero
within 300 ms of switching to 1.5x. The actual Twitch replay reported 4,721.035
seconds and then zero after switching to 2x. A recorded rapid local sequence
showed the next speed input arriving while the clock was zero; after 17 changes,
the clock remained near the VOD beginning (about 2.2 seconds), with video still
being presented. The trace is `artifacts/live-replay-rate/rapid-baseline.log`.

Isolating the rate change from its following `libvlc_media_player_set_time` call
kept both sources at their seeked positions, but the HTTP HLS Windows loopback
audio check then measured an 859 ms low-output run on a 1x to 1.25x change. The
audio resynchronization could not simply be removed. Its trace is
`artifacts/live-replay-rate/audio-without-resync.log`.

VLC's HLS clock reports zero temporarily while that resynchronization seek is
in flight. The next speed change used zero as another `set_time` target, making
the move to the VOD beginning persistent. The tab also used the raw zero sample
to reanchor its seekbar. A deterministic fake-engine test reproduced that
seekbar jump before the correction.

## Correction

The engine remembers its last HLS audio resynchronization position. A later
rate request on a seeked replay waits for the playback clock to resume advancing
near that position before submitting another seek. A transient zero or other
large backward clock sample is never used as a new resynchronization target.
The wait is bounded, cancellable, and releases the native player lock between
polls. A stalled clock at a growing playlist's published edge permits a speed
change without another seek.

The Twitch source inspection already validates the playlist's longest segment.
That measured duration now travels with the playback source, including a
rebased source. The engine avoids an audio resynchronization seek inside the
last published segment, where the input can have no next segment to read.

The tab reanchors its replay clock using the same validated sample or estimated
anchor that drives the seekbar. It cannot replace a newer seek or playback-state
anchor after the rate request completes.

## Verification

- The corrected local playback test sought to 35.25 seconds, changed speed five
  times including a change during VLC's temporary zero clock, and finished near
  45.9 seconds. Pixel callbacks showed continuing green/blue replay content and
  no red frames from the VOD beginning.
- The same test on the live Twitch archive sought to 6,290 seconds, changed speed
  nine times, and finished at 6,296.866 seconds with advancing video. The
  published-edge rate test also passed on a growing local playlist.
- The deterministic UI test kept the behind-live seekbar at its position while
  the fake VLC clock returned zero on five speed selections.
- Both file and HTTP HLS Windows loopback audio tests passed all ten rate
  transitions. The longest measured low-output run was 40 ms, below their
  existing 100 ms limit. The Release test build passed with warnings as errors.
- All 17 live first-seek checks, 34 pause/seekbar checks, and three live replay
  demuxer policy checks passed. Logs are under `artifacts/live-replay-rate`.

The broadcast URL was resolved at test time and is not stored in the repository.
