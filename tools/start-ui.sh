#!/usr/bin/env bash
# Started by the ProjectLauncher dashboard (myBrowser/utilities/caddy) via
# machines/Surface.json. --no-incremental because incremental builds over
# /mnt/c miss changed sources (DrvFS timestamps) and serve stale code.
cd /mnt/c/proj/multiImageClient
exec "$HOME/.dotnet/dotnet" run --project MultiImageClient/MultiImageClient.csproj -c Release --no-incremental -- --ui
