from pathlib import Path
import unittest

deploy = Path(__file__).parent
checker_line = "checker=/usr/local/lib/multiimageclient-update/check-unfinished-work.py\n"


def read(name):
    return (deploy / name).read_text(encoding="utf-8")


def code_lines(text):
    return [line for line in text.splitlines() if not line.lstrip().startswith("#")]


# sudoers lets the deploy user run the update helper as root, so every file the
# helper runs must be a root-owned copy that the deploy user cannot edit.
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

    def test_installer_copies_the_checker_root_owned_before_the_helper(self):
        installer = read("install-agent-deploy.sh")
        self.assertIn(checker_line, installer)
        checker = installer.index('install -o root -g root -m 0644 "$src_checker" "$checker"')
        helper = installer.index('install -o root -g root -m 0755 "$src_update" "$helper"')
        self.assertLess(checker, helper)

    def test_release_refuses_stale_copies_before_building(self):
        release = read("agent-redeploy.sh")
        self.assertIn(checker_line, release)
        build = release.index('"$dotnet" publish')
        for check in ('cmp -s "$helper" "$repo/deploy/update-shared-host.sh"',
                      'cmp -s "$checker" "$repo/deploy/check-unfinished-work.py"'):
            self.assertLess(release.index(check), release.index("run_helper busy-check"))
            self.assertLess(release.index(check), build)


if __name__ == "__main__":
    unittest.main()
