import importlib.util
from pathlib import Path
import os
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch, Mock

spec = importlib.util.spec_from_file_location("restart_local_ui", Path(__file__).resolve().parents[1] / "restart_local_ui.py")
ui = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ui)


class RestartTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.process = self.root / "proc" / "123"
        self.process.mkdir(parents=True)
        (self.process / "cwd").symlink_to(self.root)

    def process_args(self, exe, args):
        (self.process / "exe").symlink_to(exe)
        (self.process / "cmdline").write_bytes("\0".join(args).encode() + b"\0")

    def validate(self, port=5960):
        ui.validate_process(123, self.root, port, self.root / "proc")

    def test_invalid_ports_fail_before_any_process_work(self):
        for value in ["", "0", "65536", "-1", "1.5", "5960/tcp", " 5960", "9" * 100]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                ui.parse_port(value)
        self.assertEqual(ui.parse_port("05960"), 5960)

    def test_existing_apphost_is_accepted_after_a_build_replaces_it(self):
        exe = str(self.root / "MultiImageClient/bin/Release/net10.0/MultiImageClient")
        self.process_args(exe + " (deleted)", [exe, "--ui", "--ui-port", "5960", "--ui-no-open"])
        self.validate()

    def test_published_dotnet_server_is_accepted(self):
        assembly = str(self.root / ".local-ui/5960/release-example/app/MultiImageClient.dll")
        self.process_args("/usr/bin/dotnet", ["dotnet", assembly, "--ui", "--ui-port", "5960", "--ui-no-open"])
        self.validate()

    def test_unrelated_executable_is_rejected_even_with_ui_arguments(self):
        self.process_args("/usr/bin/python3", ["python3", "--ui", "--ui-port", "5960"])
        with self.assertRaisesRegex(RuntimeError, "executable"):
            self.validate()

    def test_other_checkout_is_rejected(self):
        (self.process / "cwd").unlink()
        (self.process / "cwd").symlink_to(self.root / "other")
        self.process_args("/usr/bin/dotnet", ["dotnet", "MultiImageClient.dll", "--ui"])
        with self.assertRaisesRegex(RuntimeError, "another checkout"):
            self.validate()

    def test_other_port_is_rejected(self):
        exe = str(self.root / "MultiImageClient/bin/Release/net10.0/MultiImageClient")
        self.process_args(exe, [exe, "--ui", "--ui-port", "6000"])
        with self.assertRaisesRegex(RuntimeError, "command"):
            self.validate()

    def test_unprovable_listeners_are_rejected(self):
        for output in [
            'LISTEN 0 512 0.0.0.0:5960 0.0.0.0:* users:(("x",pid=1,fd=5))',
            'LISTEN 0 512 127.0.0.1:5960 0.0.0.0:*',
            'LISTEN 0 512 127.0.0.1:5960 0.0.0.0:* users:(("x",pid=1,fd=5),("y",pid=2,fd=5))',
            'invalid',
        ]:
            with self.subTest(output=output), patch.object(ui.subprocess, "run", return_value=Mock(stdout=output)):
                with self.assertRaises(RuntimeError):
                    ui.listener_pid(5960)

    def test_build_failure_leaves_existing_process_running(self):
        with patch.object(ui, "listener_pid", return_value=123), \
             patch.object(ui.os, "pidfd_open", return_value=77), \
             patch.object(ui.os, "close"), \
             patch.object(ui, "validate_process"), \
             patch.object(ui, "stop_process") as stop, \
             patch.object(ui.subprocess, "Popen") as start, \
             patch.object(ui.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "publish")):
            with self.assertRaises(subprocess.CalledProcessError):
                ui.restart(self.root, 5960, "dotnet", self.root / "server.log")
            stop.assert_not_called()
            start.assert_not_called()
        self.assertEqual(list((self.root / ".local-ui/5960").glob("release-*")), [])

    def test_changed_port_owner_during_build_is_not_stopped(self):
        def publish(args, **kwargs):
            app = Path(args[args.index("--output") + 1])
            app.mkdir()
            (app / "MultiImageClient.dll").touch()
        with patch.object(ui, "listener_pid", side_effect=[123, 456]), \
             patch.object(ui.os, "pidfd_open", return_value=77), \
             patch.object(ui.os, "close"), \
             patch.object(ui, "validate_process"), \
             patch.object(ui, "stop_process") as stop, \
             patch.object(ui.subprocess, "run", side_effect=publish):
            with self.assertRaisesRegex(RuntimeError, "owner changed"):
                ui.restart(self.root, 5960, "dotnet", self.root / "server.log")
            stop.assert_not_called()

    def test_stop_signals_only_the_selected_process(self):
        selected = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
        neighbor = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
        try:
            fd = os.pidfd_open(selected.pid)
            try:
                ui.stop_process(fd)
            finally:
                os.close(fd)
            selected.wait(timeout=2)
            self.assertIsNone(neighbor.poll())
        finally:
            for child in [selected, neighbor]:
                if child.poll() is None:
                    child.terminate()
                child.wait(timeout=2)


if __name__ == "__main__":
    unittest.main()
