# Delayed audio when switching streams — 2026-09-20

## Confirmed switching defects

Automatic background muting (`PlaybackAudioState.Muted`) used the same native
path as explicit user muting (`HardMuted`). Both set volume to zero, set native
mute, and selected audio track `-1`. Selecting a stream again therefore had to
reselect its audio track. VLC deletes the decoder on deselection and creates a
new one on selection; this is an audio restart, not simply an output unmute.
See [VLC's elementary-stream implementation](https://github.com/videolan/vlc-3.0/blob/master/src/input/es_out.c).

There was also a cross-player race. The app requests the outgoing mute before
the incoming unmute, but separate per-player workers execute those requests
independently. VLC's default Windows MMDevice output applies mute and volume to
a shared audio session. A late outgoing mute can silence the selected stream
until its next audio convergence retry. Disabling volume persistence alone
does not give the players separate audio sessions.

A real two-player Windows loopback experiment reproduced this: muting player B
silenced both players, and unmuting A restored both. DirectSound retained A's
audio while B stayed muted, and vice versa, including separate libVLC instances
as used by native chat overlays. The source agrees: [MMDevice uses session
volume](https://github.com/videolan/vlc-3.0/blob/master/modules/audio_output/mmdevice.c),
whereas [DirectSound controls each player's buffer](https://github.com/videolan/vlc-3.0/blob/master/modules/audio_output/directsound.c).

## Changes

- Automatic mute keeps the audio decoder selected and running silently.
- Explicit user mute still disables its track. Clearing that mute while the
  stream is in the background restores the track under mute and zero volume.
- Use `--aout=directsound,none` and `--no-volume-save` so application mute and
  volume are independent per player. The final `none` prevents VLC's implicit
  fallback to the shared-session backend if DirectSound cannot initialize.
  The standard installed VLC 3.0.23 includes and successfully loads DirectSound.
- Preserve the existing version checks so stale audio requests cannot restore
  an obsolete state.

## Stream-specific investigation

Anonymous captures of the three streams in the session contained about 46
seconds each. xQc and olesyaliberman used MPEG-TS; summit1g used fragmented MP4.
All three carried continuous AAC-LC, stereo, 48 kHz audio. Their audio packet
timestamps were spaced by 21.333 ms, with no abnormal xQc audio gaps.

The selection handler already submits audio before chat/layout refresh.
The session log contained the three initial playback starts and no video-output
recreation or replay transition during subsequent multistream viewing. The
mounted video surfaces are retained when selection changes within a grid.

These findings establish faults in the application's switching mechanism.
They do not establish an xQc-specific encoder defect or a fixed delay unique to
that channel. Decoder buffering and the ordering of asynchronous audio requests
can make the old behavior vary by player and switch.

## Measurement boundaries

PCM callback probes prove decoder lifetime, continuing silent output, and
request-to-decoding timing. They do **not** measure when sound reaches Windows:
the decoded blocks carry presentation timestamps that can be over a second
ahead of the callback. Probes using `--no-video` also remove an essential active
clock when the audio track is disabled; those early stream measurements are
marked superseded in their local artifacts.

The final output probe keeps video decoding active and uses WASAPI device
timestamps with a known tone to distinguish the tested player from unrelated
desktop audio. Contaminated whole-endpoint xQc amplitude measurements are not
used as latency evidence.

Four switches per configuration, using the same AAC/MPEG-TS audio/video control
and requiring 100 ms of sustained output, measured:

| Configuration | Request to sustained Windows output |
| --- | --- |
| Original MMDevice output, deselect/reselect audio | 1,516–1,719 ms |
| DirectSound output, keep selected track muted | 31–36 ms |

These are controlled playback measurements, not a claimed measurement of the
user's original xQc delay. Three additional DirectSound cycles restored a
hard-muted track in the background; the track became selected with mute still
enabled and volume zero, and no test tone was detected over each 2.4-second
observation window.

The PCM test backend (`amem`) has a separate restart limitation: without a
custom volume callback its `Start()` does not restore software gain after the
decoder is recreated. DirectSound's `OutputStart()` explicitly restores volume
and mute. Accordingly, automatic-mute/startup/rapid-switch tests observe real
scaled PCM, while the hard-mute restoration test uses the actual Windows output
backend and is independently checked by the loopback probe.

## Validation

- Release solution build with warnings treated as errors: zero warnings/errors.
- All four new native regression checks failed against the saved original
  infrastructure binary; the existing 17 tests matched by the filter passed.
- Final Release output: all 21 tests in the native VLC filter passed, including
  all four new checks and actual Windows output isolation, with zero skips.
- Full existing suite: 783/789 passed in the first run, zero skips/timeouts.
  All six physical desktop/input failures passed isolated reruns, for 789/789
  distinct existing checks passed across the full run and focused reruns.

To include the native audio checks after building Release with the repository's
specified .NET SDK:

```powershell
$env:SVS_TEST_VLC_DIRECTORY = 'C:\Program Files\VideoLAN\VLC'
$env:SVS_TEST_AUDIO_OUTPUT = 'true'
$env:SVS_TEST_FILTER = 'native VLC'
dotnet run --no-build -c Release --project tests\StreamlinkVlcStudio.Tests
```

The output-isolation test opens the default Windows audio device using a very
quiet generated tone. The other new tests capture PCM without speaker output.

Diagnostic scripts, original engine source/binary, sample hashes, measurements,
and test logs are retained locally in `.tmp/audio-switch-diagnosis/`.
