# Reproduce the live comparison

Use an interactive Windows desktop with the SDK pinned in `global.json`, VLC
3.0.23 and Streamlink installed. Choose four or eight public URLs that are live
at the time of testing, including Twitch and Kick. The original four channel
names are recorded in `summary.json`; their future availability is not assumed.

The baseline is the pre-change working tree, including its earlier uncommitted
work. `baseline-production.zip` contains the original versions of the only
four production files changed by this optimization. To recreate it, copy the
current source into an isolated directory, extract that archive over the copy,
and keep the current test/measurement code. Do not replace files in a working
directory containing work you want to preserve.

Build each directory with `scripts/dev.ps1 Build`. Preserve each entire
`tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0` output
directory separately. Copy the updated `StreamlinkVlcStudio.Tests.dll` and its
PDB into the baseline output directory, so both versions run the identical
measurement harness against their respective application assemblies. No other
DLL is copied between versions.

From the updated repository, invoke:

```powershell
$urls = @(
    'https://www.twitch.tv/eslcs',
    'https://www.twitch.tv/valorant',
    'https://kick.com/cuffem',
    'https://kick.com/deenthegreat'
)
.\scripts\measure-live-multistream.ps1 `
    -BaselineTests 'C:\measure\before\StreamlinkVlcStudio.Tests.dll' `
    -UpdatedTests 'C:\measure\after\StreamlinkVlcStudio.Tests.dll' `
    -Urls $urls -OutputDirectory '.tmp\live-comparison' `
    -Trials 3 -WarmupSeconds 20 -Seconds 60
```

The script refuses different harness DLL hashes or existing trial output paths.
It restores its process environment afterwards and stops on a failed trial.
Keep failed logs and investigate provider availability or playback failures;
do not discard an unfavorable observation. Each run requests best quality,
opens all streams and native chat, waits for playing video, warms up, samples
the complete observed process tree, captures the window after measurement,
then closes everything and checks descendant-process cleanup.

`results.json` includes actual dimensions, displayed/lost video pictures,
decoded/lost audio, chat counts, audio selection, hidden/visible docked row
counts, CPU/memory/I/O/GPU samples, managed allocations, GC counts and hashes.
Chat counts are retained message counts (capped at 100), not arrival rates.
Do not treat a passing smoke assertion (at least 20 displayed fps per stream)
as proof of source cadence: inspect the actual per-stream frame deltas.

The raw archive includes all six formal trials, post-trial screenshots, cleanup
records, separate aggregate allocation profiles, regression logs, source
manifest and machine details. Profiler traces and provider application logs
remain local; they are not needed to review the counters. The included
`summarize-live.py` recomputes the tables from `*-*/results.json` for either
the original candidate's named directories or the final matrix's numeric ones.

Validation commands:

```powershell
.\scripts\dev.ps1 Check
.\scripts\dev.ps1 Test -Filter 'multistream UI resources:' -Interactive -NoBuild
.\scripts\dev.ps1 Test -Filter 'docked' -Interactive -NoBuild
$env:SVS_TEST_VLC_DIRECTORY = 'C:\Program Files\VideoLAN\VLC'
$env:SVS_TEST_VLC_MEDIA = 'C:\fixtures\blue.mp4'
.\scripts\dev.ps1 Test -Filter 'window sharing' -Interactive -NoBuild
.\scripts\dev.ps1 Test -Filter 'responsive main window keeps real' -Interactive -NoBuild
.\scripts\dev.ps1 Test -Filter 'replay seek overlay' -Interactive -NoBuild
.\scripts\dev.ps1 Test -Filter 'picture-in-picture resize' -Interactive -NoBuild
```

The VLC regression fixture is a 60-second, 960x540, 24 fps solid blue H.264 video.
For example, create it with FFmpeg:

```powershell
ffmpeg -f lavfi -i 'color=c=blue:s=960x540:r=24:d=60' -c:v libx264 -pix_fmt yuv420p -an blue.mp4
```

It supplements the real-stream measurements with deterministic pixel and
physical-input assertions.
