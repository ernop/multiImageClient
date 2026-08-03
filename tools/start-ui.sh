#!/usr/bin/env bash
# Started by the ProjectLauncher dashboard (myBrowser/utilities/caddy) via
# machines/Surface.json. The build step uses --no-incremental because
# incremental builds over /mnt/c miss changed sources (DrvFS timestamps) and
# serve stale code. Build and run are separate steps: `dotnet run` has no
# --no-incremental flag and forwards it to the app, which rejects it and
# exits with its usage text (observed 2026-08-03).
cd /mnt/c/proj/multiImageClient
"$HOME/.dotnet/dotnet" build MultiImageClient/MultiImageClient.csproj -c Release --no-incremental || exit 1
exec "$HOME/.dotnet/dotnet" run --project MultiImageClient/MultiImageClient.csproj -c Release --no-build -- --ui
