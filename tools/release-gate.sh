#!/usr/bin/env bash
set -Eeuo pipefail

# Local release gate documented in deploy/README.md.
# Runs JS syntax checks across all frontend modules, JS unit tests,
# Python deploy tests, C# unit tests, and a clean-worktree Release build.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_bin="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
if [[ ! -x "$dotnet_bin" ]]; then
    dotnet_bin="$(command -v dotnet)"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

cd "$repo_root"

echo "==> JS syntax check"
for js in MultiImageClient/Ui/wwwroot/*.js; do
    node --check "$js"
done

echo "==> JS unit tests"
node --test MultiImageClient/Ui/tests/*.test.js

echo "==> Python deploy tests"
python3 -m unittest discover -s deploy -p 'test_*.py'

echo "==> C# unit tests"
"$dotnet_bin" test MultiImageClient.sln --no-restore

if [[ "${1:-}" != "--skip-worktree-release" ]]; then
    echo "==> Clean-worktree Release build"
    wt="$(mktemp -d)"
    cleanup() {
        git worktree remove --force "$wt" 2>/dev/null || rm -rf "$wt"
    }
    trap cleanup EXIT
    git worktree add -q --detach "$wt" HEAD
    (cd "$wt" && "$dotnet_bin" build MultiImageClient.sln -c Release)
fi

echo "==> Release gate passed"
