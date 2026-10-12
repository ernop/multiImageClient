import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("unfinished", Path(__file__).with_name("check-unfinished-work.py"))
unfinished = importlib.util.module_from_spec(spec)
spec.loader.exec_module(unfinished)


def write_job(base, job_id, lines, modified):
    folder = Path(base) / "UiHistory" / job_id
    folder.mkdir(parents=True)
    events = folder / "events.jsonl"
    events.write_text("".join(line + "\n" for line in lines), encoding="utf-8")
    os.utime(events, (modified, modified))


def write_loop(base, loop_id, text):
    folder = Path(base) / "UiGoalLoops" / loop_id
    folder.mkdir(parents=True)
    (folder / "loop.json").write_text(text, encoding="utf-8")


accepted = json.dumps({"type": "accepted", "gens": ["gpt2"]})
done = json.dumps({"type": "job-done"})
interrupted = json.dumps({"type": "job-done", "interrupted": True})


class UnfinishedWorkTests(unittest.TestCase):
    def test_counts_only_current_jobs_without_job_done(self):
        started = time.time() - 600
        with tempfile.TemporaryDirectory() as base:
            write_job(base, "running", [accepted], started + 60)
            write_job(base, "finished", [accepted, done], started + 60)
            write_job(base, "closed-at-load", [accepted, interrupted], started + 60)
            # An earlier process cannot still run this job.
            write_job(base, "earlier-process", [accepted], started - 60)
            self.assertEqual(unfinished.unfinished_work(base, started), (1, 0))

    def test_unreadable_lines_never_count_as_job_done(self):
        started = time.time() - 600
        with tempfile.TemporaryDirectory() as base:
            write_job(base, "mid-append", [accepted, '{"type":"job-do'], started + 60)
            write_job(base, "cut-then-closed", [accepted, '{"type":"gen-res', interrupted], started + 60)
            self.assertEqual(unfinished.unfinished_work(base, started), (1, 0))

    def test_running_and_corrupt_goal_loops_count_regardless_of_age(self):
        with tempfile.TemporaryDirectory() as base:
            write_loop(base, "running", json.dumps({"id": "a", "status": "running"}))
            write_loop(base, "stopped", json.dumps({"id": "b", "status": "stopped"}))
            write_loop(base, "done", json.dumps({"id": "c", "status": "done"}))
            write_loop(base, "corrupt", '{"id": "d", "stat')
            for path in Path(base).glob("UiGoalLoops/*/loop.json"):
                os.utime(path, (0, 0))
            self.assertEqual(unfinished.unfinished_work(base, time.time()), (0, 2))

    def test_missing_history_folders_are_idle(self):
        with tempfile.TemporaryDirectory() as base:
            self.assertEqual(unfinished.unfinished_work(base, time.time()), (0, 0))


class UnitStateTests(unittest.TestCase):
    def test_settings_path_comes_from_the_unit_environment(self):
        environment = 'DOTNET_ROOT=/home/tparkour/.dotnet MULTIIMAGECLIENT_SETTINGS=/etc/multiimageclient/settings.json'
        self.assertEqual(unfinished.settings_path(environment), "/etc/multiimageclient/settings.json")
        self.assertEqual(unfinished.settings_path('"MULTIIMAGECLIENT_SETTINGS=/etc/x y/settings.json"'),
                         "/etc/x y/settings.json")
        with self.assertRaises(RuntimeError):
            unfinished.settings_path("DOTNET_ROOT=/home/tparkour/.dotnet")

    def test_process_start_needs_a_recorded_timestamp(self):
        with self.assertRaises(RuntimeError):
            unfinished.process_started_at("0")
        now = int(time.monotonic() * 1_000_000)
        self.assertAlmostEqual(unfinished.process_started_at(str(now), margin_seconds=5), time.time() - 5, delta=1)

    def fake_show(self, values):
        return lambda unit, prop: values[prop]

    def test_stopped_or_sleeping_units_are_idle_without_reading_history(self):
        for state in ("inactive", "failed"):
            with patch.object(unfinished, "show", self.fake_show({"LoadState": "loaded", "ActiveState": state})):
                idle, description = unfinished.unit_status("multiimageclient-ui")
            self.assertTrue(idle)
            self.assertEqual(description, "multiimageclient-ui: idle (no running process)")

    def test_transitional_states_are_busy(self):
        with patch.object(unfinished, "show", self.fake_show({"LoadState": "loaded", "ActiveState": "deactivating"})):
            self.assertEqual(unfinished.unit_status("multiimageclient-ui"),
                             (False, "multiimageclient-ui: service is deactivating"))

    def test_unknown_unit_is_an_error(self):
        with patch.object(unfinished, "show", self.fake_show({"LoadState": "not-found", "ActiveState": "inactive"})):
            with self.assertRaises(RuntimeError):
                unfinished.unit_status("multiimageclient-typo")

    def test_active_unit_counts_work_since_its_process_started(self):
        with tempfile.TemporaryDirectory() as root:
            base = Path(root) / "saves"
            settings = Path(root) / "settings.json"
            settings.write_text(json.dumps({"ImageDownloadBaseFolder": str(base)}), encoding="utf-8")
            started_monotonic = int((time.monotonic() - 600) * 1_000_000)
            write_job(base, "running", [accepted], time.time() - 30)
            write_job(base, "earlier-process", [accepted], time.time() - 3600)
            write_loop(base, "loop", json.dumps({"status": "running"}))
            values = {"LoadState": "loaded", "ActiveState": "active",
                      "Environment": f"MULTIIMAGECLIENT_SETTINGS={settings}",
                      "ExecMainStartTimestampMonotonic": str(started_monotonic)}
            with patch.object(unfinished, "show", self.fake_show(values)):
                self.assertEqual(unfinished.unit_status("multiimageclient-ui"),
                                 (False, "multiimageclient-ui: 1 unfinished job(s), 1 running goal loop(s)"))


class MainTests(unittest.TestCase):
    def run_main(self, statuses, *arguments):
        with patch.object(unfinished, "unit_status", side_effect=statuses), \
                patch.object(unfinished.time, "sleep") as sleep, \
                patch("sys.argv", ["check-unfinished-work.py", *arguments, "multiimageclient-ui"]), \
                patch("sys.stdout", new_callable=io.StringIO), \
                patch("sys.stderr", new_callable=io.StringIO) as stderr:
            return unfinished.main(), sleep.call_count, stderr.getvalue()

    def test_idle_exits_zero(self):
        code, _, _ = self.run_main([(True, "multiimageclient-ui: idle (no running process)")])
        self.assertEqual(code, 0)

    def test_busy_without_waiting_exits_tempfail_and_changes_nothing(self):
        code, sleeps, stderr = self.run_main([(False, "multiimageclient-ui: 1 unfinished job(s), 0 running goal loop(s)")])
        self.assertEqual((code, sleeps), (75, 0))
        self.assertIn("BUSY", stderr)

    def test_waiting_proceeds_once_the_unit_becomes_idle(self):
        busy = (False, "multiimageclient-ui: 1 unfinished job(s), 0 running goal loop(s)")
        idle = (True, "multiimageclient-ui: 0 unfinished job(s), 0 running goal loop(s)")
        code, sleeps, _ = self.run_main([busy, busy, idle], "--wait-minutes", "60")
        self.assertEqual((code, sleeps), (0, 2))

    def test_waiting_gives_up_at_the_deadline(self):
        busy = (False, "multiimageclient-ui: 0 unfinished job(s), 1 running goal loop(s)")
        with patch.object(unfinished.time, "monotonic", side_effect=[0, 0, 61]):
            code, sleeps, stderr = self.run_main([busy, busy], "--wait-minutes", "1")
        self.assertEqual((code, sleeps), (75, 1))
        self.assertIn("BUSY", stderr)

    def test_unreadable_state_exits_two(self):
        code, _, stderr = self.run_main(RuntimeError("the unit sets no MULTIIMAGECLIENT_SETTINGS"))
        self.assertEqual(code, 2)
        self.assertIn("cannot read instance state", stderr)


if __name__ == "__main__":
    unittest.main()
