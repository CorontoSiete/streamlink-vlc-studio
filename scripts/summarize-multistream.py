"""Summarize alternating whole-application trials without mixing subsystem benchmarks."""
import argparse
import json
from pathlib import Path
from statistics import mean, median
from measurement_helpers import format_percent, read_json, reduction, require, require_stream_values

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('directory', type=Path)
p.add_argument('--streams', type=int, nargs='+', choices=(4, 8), default=(4, 8))
p.add_argument('--chat', nargs='+', choices=('quiet', 'busy', 'animated'), default=('quiet', 'busy', 'animated'))
p.add_argument('--trials', type=int, default=3)
p.add_argument('--warmup', type=int, default=10)
p.add_argument('--seconds', type=int, default=30)
p.add_argument('--require-gpu-memory', action='store_true', help='Reject missing/invalid GPU counters instead of reporting unavailable data')
a = p.parse_args()
require(a.trials > 0 and a.warmup >= 0 and a.seconds > 0,
        'Invalid trial, warm-up, or sample count.')
rows = []
binary_hashes = {'before': set(), 'after': set()}
renderer_hashes = set()
for streams in a.streams:
    for mode in a.chat:
        versions = {}
        for version in ('before', 'after'):
            trials = [read_json(a.directory / f'{streams}-{mode}-{trial}-{version}.json')
                      for trial in range(1, a.trials + 1)]
            require(len({t['applicationSha256'] for t in trials}) == 1 and
                    len({t['infrastructureSha256'] for t in trials}) == 1,
                    f'The {version} production binaries changed between trials.')
            values = []
            for trial in trials:
                require(trial['warmupSeconds'] == a.warmup and a.seconds <= trial['measuredSeconds'] < a.seconds + 1,
                        'Unexpected warm-up or sample duration.')
                require(trial['streams'] == streams and trial['chat'] == mode and trial['decoder'] == 'dxva2',
                        'The workload or decoder differs from the requested measurement.')
                require((trial['width'], trial['height'], trial['fps']) == (1920, 1080, 60),
                        'The source must retain 1920x1080 at 60 fps.')
                require_stream_values(trial, ('frames', 'chatFrames'), streams,
                                      f'{streams}-{mode}-{version}')
                binary_hashes[version].add((trial['applicationSha256'], trial['infrastructureSha256']))
                renderer_hashes.add(trial['rendererSha256'])
                require(all(f['lost'] == 0 for f in trial['frames']),
                        'A trial lost pictures; do not silently exclude it.')
                samples = trial['samples']
                require(len(samples) >= a.seconds * 0.9, 'Too few measurement samples.')
                if a.require_gpu_memory:
                    require(not trial['gpuError'], f"GPU counters failed: {trial['gpuError']}")
                    require(all(s.get('gpuMemory', {}).get('DedicatedBytes', 0) > 0 for s in samples),
                            'GPU memory counters are incomplete.')
                elapsed = trial['measuredSeconds']
                pids = {p['Pid'] for p in trial['beforeProcesses']}
                require(pids and pids == {p['Pid'] for p in trial['afterProcesses']} and
                        all(pids == {p['Pid'] for p in s['processes']} for s in samples),
                        'Missing processes or process churn invalidates a simple CPU delta.')
                require(all(f['displayed'] >= 55 * elapsed for f in trial['frames']),
                        'A stream did not retain 55 displayed fps.')
                require(all(frames >= 10 * elapsed for frames in trial['chatFrames']),
                        'A stream stopped receiving native chat frames.')
                visibility = read_json(a.directory / f'{streams}-{mode}-{len(values) + 1}-{version}.visibility.json')
                require(len(visibility) == streams and all(p['GlyphPixels'] >= 5 for p in visibility),
                        'Chat must remain visible on every pane.')
                values.append(dict(
                    cpu_cores=trial['cpuCores'],
                    private_mib=mean(sum(p['PrivateBytes'] for p in s['processes']) for s in samples) / 2**20,
                    working_set_mib=mean(sum(p['WorkingSetBytes'] for p in s['processes']) for s in samples) / 2**20,
                    managed_mib_per_second=trial['managedAllocatedBytes'] / elapsed / 2**20,
                    hls_mib_per_second=trial['hlsBytes'] / elapsed / 2**20,
                    pipe_mib_per_second=trial['pipeBytes'] / elapsed / 2**20,
                    io_read_mib_per_second=(sum(p['ReadBytes'] for p in trial['afterProcesses']) -
                                            sum(p['ReadBytes'] for p in trial['beforeProcesses'])) / elapsed / 2**20,
                    io_write_mib_per_second=(sum(p['WriteBytes'] for p in trial['afterProcesses']) -
                                             sum(p['WriteBytes'] for p in trial['beforeProcesses'])) / elapsed / 2**20,
                    gpu_decode_percent=mean(s['gpu'].get('VideoDecode', 0) for s in samples) if not trial['gpuError'] else None,
                    gpu_3d_percent=mean(s['gpu'].get('3D', 0) for s in samples) if not trial['gpuError'] else None,
                    gpu_dedicated_mib=mean(s['gpuMemory']['DedicatedBytes'] for s in samples) / 2**20
                        if not trial['gpuError'] and all('gpuMemory' in s for s in samples) else None,
                    gpu_shared_mib=mean(s['gpuMemory']['SharedBytes'] for s in samples) / 2**20
                        if not trial['gpuError'] and all('gpuMemory' in s for s in samples) else None,
                    process_count=mean(len(s['processes']) for s in samples),
                    minimum_displayed_fps=min(f['displayed'] / elapsed for f in trial['frames']),
                    lost_pictures=sum(f['lost'] for f in trial['frames']),
                    lost_audio_buffers=sum(f['audioLost'] for f in trial['frames']),
                    chat_frames_per_second=mean(trial['chatFrames']) / elapsed))
            versions[version] = dict(trials=values, medians={
                k: median(v[k] for v in values) if all(v[k] is not None for v in values) else None
                for k in values[0]})
        before, after = (versions[v]['medians'] for v in ('before', 'after'))
        rows.append(dict(streams=streams, chat=mode, **versions,
                         cpu_reduction_percent=reduction(before['cpu_cores'], after['cpu_cores']),
                         private_mib_reduction=before['private_mib'] - after['private_mib']))
require(all(len(hashes) == 1 for hashes in binary_hashes.values()), 'Production binaries changed between workloads.')
require(len(renderer_hashes) == 1, 'Both versions must receive the same native chat workload.')
(a.directory / 'summary.json').write_text(json.dumps(rows, indent=2) + '\n', encoding='utf-8')
print('| Streams | Chat | CPU cores before / after | Private MiB before / after | CPU change |')
print('| --- | --- | --- | --- | --- |')
for row in rows:
    before, after = (row[v]['medians'] for v in ('before', 'after'))
    change = row['cpu_reduction_percent']
    print(f"| {row['streams']} | {row['chat']} | {before['cpu_cores']:.3f} / {after['cpu_cores']:.3f} | "
          f"{before['private_mib']:.1f} / {after['private_mib']:.1f} | {format_percent(-change if change is not None else None)} |")
if all(row[v]['medians']['gpu_dedicated_mib'] is not None for row in rows for v in ('before', 'after')):
    print('\n| Streams | Chat | Dedicated GPU MiB before / after | Shared GPU MiB before / after | 3D % before / after | Decode % before / after |')
    print('| --- | --- | --- | --- | --- | --- |')
    for row in rows:
        before, after = (row[v]['medians'] for v in ('before', 'after'))
        print(f"| {row['streams']} | {row['chat']} | {before['gpu_dedicated_mib']:.1f} / {after['gpu_dedicated_mib']:.1f} | "
              f"{before['gpu_shared_mib']:.1f} / {after['gpu_shared_mib']:.1f} | "
              f"{before['gpu_3d_percent']:.2f} / {after['gpu_3d_percent']:.2f} | "
              f"{before['gpu_decode_percent']:.2f} / {after['gpu_decode_percent']:.2f} |")
