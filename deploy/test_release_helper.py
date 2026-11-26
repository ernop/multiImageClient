from pathlib import Path
import unittest

deploy = Path(__file__).parent
checker_line = "checker=/usr/local/lib/multiimageclient-update/check-unfinished-work.py\n"
env_tool_line = "env_tool=/usr/local/lib/multiimageclient-update/create-environment.py\n"
vibecoders_line = "vibecoders=/usr/local/lib/multiimageclient-update/update-vibecoders.sh\n"


def read(name):
    return (deploy / name).read_text(encoding="utf-8")


def code_lines(text):
    return [line for line in text.splitlines() if not line.lstrip().startswith("#")]


# Every file executed as root during a release must be a root-owned copy that
# the deploy user cannot edit.
class ReleaseHelperTests(unittest.TestCase):
    def test_helper_runs_only_the_root_owned_checker_copy(self):
        helper = read("update-shared-host.sh")
        self.assertIn(checker_line, helper)
        code = "\n".join(code_lines(helper))
        self.assertNotIn("BASH_SOURCE", code)
        self.assertNotIn("check-unfinished-work.py", code.replace(checker_line.strip(), ""))
        runs = [line for line in code_lines(helper) if "python" in line]
        self.assertEqual(len(runs), 2)
        for line in runs:
            self.assertIn('/usr/bin/python3 -I "$checker"', line)

    def test_vibecoders_runs_only_root_owned_tools_and_checks_staleness(self):
        script = read("update-vibecoders.sh")
        for expected in (checker_line, env_tool_line, vibecoders_line):
            self.assertIn(expected, script)
        code = "\n".join(code_lines(script))
        self.assertNotIn("BASH_SOURCE", code)
        python_runs = [line for line in code_lines(script) if "python" in line]
        self.assertEqual(len(python_runs), 3)
        self.assertIn('/usr/bin/python3 -I "$checker"', python_runs[0])
        self.assertIn('/usr/bin/python3 -I "$env_tool"', python_runs[1])
        self.assertIn('/usr/bin/python3 -I - "$manifest"', python_runs[2])
        first_run = script.index('/usr/bin/python3 -I "$checker"')
        for check in ('cmp -s "$vibecoders" "$repo/deploy/update-vibecoders.sh"',
                      'cmp -s "$checker" "$repo/deploy/check-unfinished-work.py"',
                      'cmp -s "$env_tool" "$repo/deploy/create-environment.py"'):
            self.assertLess(script.index(check), first_run)

    def test_installer_copies_dependencies_root_owned_before_callers(self):
        installer = read("install-agent-deploy.sh")
        for expected in (checker_line, env_tool_line, vibecoders_line):
            self.assertIn(expected, installer)
        checker = installer.index('install -o root -g root -m 0644 "$src_checker" "$checker"')
        env_tool = installer.index('install -o root -g root -m 0644 "$src_env_tool" "$env_tool"')
        vibecoders = installer.index('install -o root -g root -m 0755 "$src_vibecoders" "$vibecoders"')
        helper = installer.index('install -o root -g root -m 0755 "$src_update" "$helper"')
        self.assertLess(checker, helper)
        self.assertLess(checker, vibecoders)
        self.assertLess(env_tool, vibecoders)

    def test_release_refuses_stale_copies_before_building(self):
        release = read("agent-redeploy.sh")
        for expected in (checker_line, env_tool_line, vibecoders_line):
            self.assertIn(expected, release)
        build = release.index('"$dotnet" publish')
        for check in ('cmp -s "$helper" "$repo/deploy/update-shared-host.sh"',
                      'cmp -s "$checker" "$repo/deploy/check-unfinished-work.py"',
                      'cmp -s "$env_tool" "$repo/deploy/create-environment.py"',
                      'cmp -s "$vibecoders" "$repo/deploy/update-vibecoders.sh"'):
            self.assertLess(release.index(check), release.index("run_helper busy-check"))
            self.assertLess(release.index(check), build)


if __name__ == "__main__":
    unittest.main()
