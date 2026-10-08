"""Validate and summarize the alternating live-stream runs saved by the PowerShell harness."""

import argparse
import json
from pathlib import Path
from statistics import mean, median
from measurement_helpers import format_percent, read_json, reduction, require, require_stream_values


def load_run(directory, streams, trial, version, warmup, seconds):
    run_directory = directory / f"{streams}-{trial}-{version}"
    result_path = run_directory / "results.json"
    payload = read_json(result_path)
    require(isinstance(payload, list) and len(payload) == 1,
            f"Expected one completed live cycle in {result_path}.")
    row = payload[0]

    require(row["cycle"] == 0 and row["version"] == version,
            f"Wrong cycle or build label in {result_path}.")
    require(row["warmupSeconds"] == warmup and seconds <= row["measuredSeconds"] < seconds + 1,
            f"Unexpected warm-up or sample duration in {result_path}.")
    require_stream_values(row, ("channels", "qualities", "dimensions", "displayed", "lost",
                                "audioLost", "audioSelected", "chatMessages"), streams, result_path)
    require(all(quality == "best" for quality in row["qualities"]),
            f"A stream did not retain best quality in {result_path}.")
    require(all(dimension["width"] > 0 and dimension["height"] > 0 for dimension in row["dimensions"]),
            f"A stream did not report a decoded picture size in {result_path}.")
    require(all(pictures >= 20 * row["measuredSeconds"] for pictures in row["displayed"]),
            f"A stream did not keep advancing in {result_path}.")
    require(all(lost == 0 for lost in row["lost"]), f"A stream lost video pictures in {result_path}.")
    require(all(lost == 0 for lost in row["audioLost"]), f"A stream lost audio buffers in {result_path}.")
    require(sum(bool(selected) for selected in row["audioSelected"]) == 1,
            f"Expected one unmuted stream in {result_path}.")
    require(row["gpuError"] is None, f"GPU counters failed in {result_path}: {row['gpuError']}")
    require(len(row["samples"]) >= int(seconds * 0.9), f"Too few measurement samples in {result_path}.")

    gpu_3d = []
    gpu_decode = []
    dedicated = []
    shared = []
    private_memory = []
    working_set = []
    sample_pids = []
    for sample in row["samples"]:
        require("3D" in sample["gpu"] and "VideoDecode" in sample["gpu"],
                f"GPU engine counters are incomplete in {result_path}.")
        memory = sample["gpuMemory"]
        require(memory["DedicatedBytes"] > 0 and memory["SharedBytes"] > 0,
                f"GPU memory counters are incomplete in {result_path}.")
        gpu_3d.append(sample["gpu"]["3D"])
        gpu_decode.append(sample["gpu"]["VideoDecode"])
        dedicated.append(memory["DedicatedBytes"] / 2**20)
        shared.append(memory["SharedBytes"] / 2**20)
        processes = sample["processes"]
        private_memory.append(sum(process["PrivateBytes"] for process in processes) / 2**20)
        working_set.append(sum(process["WorkingSetBytes"] for process in processes) / 2**20)
        sample_pids.append(tuple(sorted(process["Pid"] for process in processes)))

    cleanup_path = run_directory / "cycle-0-cleanup.json"
    cleanup = read_json(cleanup_path)
    require(cleanup.get("allObservedChildrenExited") is True,
            f"Playback child cleanup did not pass in {cleanup_path}.")

    elapsed = row["measuredSeconds"]
    return {
        "trial": trial,
        "version": version,
        "cpu_cores": row["cpuCores"],
        "gpu_3d_percent": mean(gpu_3d),
        "gpu_decode_percent": mean(gpu_decode),
        "gpu_dedicated_mib": mean(dedicated),
        "gpu_shared_mib": mean(shared),
        "private_mib": mean(private_memory),
        "working_set_mib": mean(working_set),
        "minimum_displayed_fps": min(pictures / elapsed for pictures in row["displayed"]),
        "lost_pictures": sum(row["lost"]),
        "lost_audio_buffers": sum(row["audioLost"]),
        "chat_messages": row["chatMessages"],
        "dimensions": row["dimensions"],
        "qualities": row["qualities"],
        "channels": row["channels"],
        "gpu_error": row["gpuError"],
        "tests_sha256": row["testsSha256"],
        "application_sha256": row["applicationSha256"],
        "infrastructure_sha256": row["infrastructureSha256"],
        "process_pid_sample_changes": sum(pids != sample_pids[0] for pids in sample_pids[1:]),
    }


def summarize(runs):
    keys = (
        "cpu_cores", "gpu_3d_percent", "gpu_decode_percent", "gpu_dedicated_mib",
        "gpu_shared_mib", "private_mib", "working_set_mib", "minimum_displayed_fps",
    )
    return {key: median(run[key] for run in runs) for key in keys}


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("directory", type=Path)
parser.add_argument("--streams", type=int, default=4)
parser.add_argument("--trials", type=int, default=3)
parser.add_argument("--warmup", type=int, default=20)
parser.add_argument("--seconds", type=int, default=60)
args = parser.parse_args()
require(args.streams in (4, 8) and args.trials > 0 and args.warmup >= 0 and args.seconds > 0,
        "Invalid stream, trial, warm-up, or sample count.")

versions = {version: [load_run(args.directory, args.streams, trial, version, args.warmup, args.seconds)
                      for trial in range(1, args.trials + 1)]
            for version in ("before", "after")}
all_runs = versions["before"] + versions["after"]
require(len({run["tests_sha256"] for run in all_runs}) == 1,
        "Before and after must use the same test/measurement assembly.")
for version, runs in versions.items():
    require(len({run["application_sha256"] for run in runs}) == 1 and
            len({run["infrastructure_sha256"] for run in runs}) == 1,
            f"The {version} production binaries changed between trials.")
require(len({tuple(run["channels"]) for run in all_runs}) == 1,
        "The before and after trials did not use the same live sources.")
require(len({tuple((d["width"], d["height"]) for d in run["dimensions"]) for run in all_runs}) == 1,
        "The decoded dimensions changed during the paired live comparison.")
require(len({tuple(run["qualities"]) for run in all_runs}) == 1,
        "The selected source quality changed during the paired live comparison.")

medians = {version: summarize(runs) for version, runs in versions.items()}
before = medians["before"]
after = medians["after"]
summary = {
    "streams": args.streams,
    "trials": args.trials,
    "warmup_seconds": args.warmup,
    "sample_seconds": args.seconds,
    "sources": all_runs[0]["channels"],
    "qualities": all_runs[0]["qualities"],
    "dimensions": all_runs[0]["dimensions"],
    "before": {"trials": versions["before"], "medians": before},
    "after": {"trials": versions["after"], "medians": after},
    "reductions_percent": {
        key: reduction(before[key], after[key]) for key in
        ("cpu_cores", "gpu_3d_percent", "gpu_dedicated_mib", "gpu_shared_mib")
    },
}
(args.directory / "summary.json").write_text(json.dumps(summary, indent=2, allow_nan=False) + "\n", encoding="utf-8")

print("| Build | CPU cores | Dedicated GPU MiB | Shared GPU MiB | GPU 3D % | Video decode % | Minimum displayed FPS |")
print("| --- | ---: | ---: | ---: | ---: | ---: | ---: |")
for version in ("before", "after"):
    row = medians[version]
    print(f"| {version} | {row['cpu_cores']:.3f} | {row['gpu_dedicated_mib']:.1f} | "
          f"{row['gpu_shared_mib']:.1f} | {row['gpu_3d_percent']:.2f} | "
          f"{row['gpu_decode_percent']:.2f} | {row['minimum_displayed_fps']:.2f} |")
print("\n| Metric | Reduction / change after update |")
print("| --- | ---: |")
for key, label in (("cpu_cores", "CPU"), ("gpu_3d_percent", "GPU 3D"),
                   ("gpu_dedicated_mib", "Dedicated GPU memory"), ("gpu_shared_mib", "Shared GPU memory")):
    value = summary["reductions_percent"][key]
    print(f"| {label} | {format_percent(value)} |")
print(f"\nValidated {args.trials * 2} live trials: best quality, consistent dimensions, zero lost video/audio, "
      "complete GPU counters, and all observed child processes exited.")
