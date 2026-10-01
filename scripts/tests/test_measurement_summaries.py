"""Run the measurement CLIs against small, controlled result files."""

import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPTS = Path(__file__).resolve().parents[1]
STREAMS = 4
PROCESS = {
    "Pid": 123, "PrivateBytes": 2**20, "WorkingSetBytes": 2**20,
    "ReadBytes": 0, "WriteBytes": 0,
}
SAMPLE = {
    "gpu": {"3D": 1, "VideoDecode": 2},
    "gpuMemory": {"DedicatedBytes": 2**20, "SharedBytes": 2**20},
    "processes": [PROCESS],
}


class MeasurementSummaryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="StreamStudio-measurement-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)

    def write_json(self, path, value, bom=False):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value), encoding="utf-8-sig" if bom else "utf-8")

    def run_summary(self, live=False, optimized=False):
        script = "summarize-live-multistream.py" if live else "summarize-multistream.py"
        command = [sys.executable]
        if optimized:
            command.append("-O")
        command += [str(SCRIPTS / script), str(self.directory), "--streams", str(STREAMS),
                    "--trials", "1", "--warmup", "0", "--seconds", "1"]
        if not live:
            command += ["--chat", "quiet"]
        return subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=10)

    def controlled_trial(self):
        return {
            "applicationSha256": "app", "infrastructureSha256": "infrastructure",
            "rendererSha256": "renderer", "warmupSeconds": 0, "measuredSeconds": 1,
            "streams": STREAMS, "chat": "quiet", "decoder": "dxva2",
            "width": 1920, "height": 1080, "fps": 60, "cpuCores": 1,
            "frames": [{"lost": 0, "displayed": 60, "audioLost": 0} for _ in range(STREAMS)],
            "chatFrames": [12] * STREAMS, "samples": [copy.deepcopy(SAMPLE)], "gpuError": None,
            "beforeProcesses": [copy.deepcopy(PROCESS)], "afterProcesses": [copy.deepcopy(PROCESS)],
            "managedAllocatedBytes": 0, "hlsBytes": 0, "pipeBytes": 0,
        }

    def write_controlled(self, change=None, bom=False):
        for version in ("before", "after"):
            trial = self.controlled_trial()
            if change:
                change(trial)
            stem = f"{STREAMS}-quiet-1-{version}"
            self.write_json(self.directory / f"{stem}.json", trial, bom)
            self.write_json(self.directory / f"{stem}.visibility.json", [{"GlyphPixels": 5}] * STREAMS, bom)

    def write_live(self, change=None, bom=False):
        for version in ("before", "after"):
            trial = {
                "cycle": 0, "version": version, "warmupSeconds": 0, "measuredSeconds": 1,
                "channels": [f"channel-{index}" for index in range(STREAMS)], "qualities": ["best"] * STREAMS,
                "dimensions": [{"width": 1920, "height": 1080}] * STREAMS,
                "displayed": [60] * STREAMS, "lost": [0] * STREAMS, "audioLost": [0] * STREAMS,
                "audioSelected": [True, False, False, False], "gpuError": None,
                "samples": [copy.deepcopy(SAMPLE)], "cpuCores": 1, "chatMessages": [0] * STREAMS,
                "testsSha256": "tests", "applicationSha256": "app", "infrastructureSha256": "infrastructure",
            }
            if change:
                change(trial)
            directory = self.directory / f"{STREAMS}-1-{version}"
            self.write_json(directory / "results.json", [trial], bom)
            self.write_json(directory / "cycle-0-cleanup.json", {"allObservedChildrenExited": True}, bom)

    def test_valid_summaries_preserve_expected_values(self):
        for live in (False, True):
            with self.subTest(live=live):
                (self.write_live if live else self.write_controlled)()
                result = self.run_summary(live=live)
                self.assertEqual(result.returncode, 0, result.stderr)
                summary = json.loads((self.directory / "summary.json").read_text(encoding="utf-8"))
                row = summary if live else summary[0]
                self.assertEqual(row["before"]["medians"]["cpu_cores"], 1)
                self.assertEqual(row["before"]["medians"]["private_mib"], 1)

    def test_controlled_validation_survives_python_optimization(self):
        self.write_controlled(lambda trial: trial["frames"][0].update(lost=1))
        for optimized in (False, True):
            with self.subTest(optimized=optimized):
                result = self.run_summary(optimized=optimized)
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertFalse((self.directory / "summary.json").exists())

    def test_summaries_reject_missing_stream_counters(self):
        for live, field in ((False, "frames"), (False, "chatFrames"),
                            (True, "lost"), (True, "audioLost"), (True, "audioSelected")):
            with self.subTest(live=live, field=field):
                (self.directory / "summary.json").unlink(missing_ok=True)
                (self.write_live if live else self.write_controlled)(lambda trial: trial[field].pop())
                result = self.run_summary(live=live)
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertFalse((self.directory / "summary.json").exists())

    def test_summaries_read_powershell_utf8_bom(self):
        for live in (False, True):
            with self.subTest(live=live):
                (self.write_live if live else self.write_controlled)(bom=True)
                result = self.run_summary(live=live)
                self.assertEqual(result.returncode, 0, result.stderr)

    def test_zero_baselines_report_unavailable_reductions(self):
        for live in (False, True):
            with self.subTest(live=live):
                def zero_baselines(trial):
                    trial["cpuCores"] = 0
                    for sample in trial["samples"]:
                        sample["gpu"]["3D"] = 0
                (self.write_live if live else self.write_controlled)(zero_baselines)
                result = self.run_summary(live=live)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn("unavailable", result.stdout)


if __name__ == "__main__":
    unittest.main()
