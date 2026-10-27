#!/usr/bin/env bash
# Runs the engine tests: makes test files, starts the test server, runs every check. Needs dotnet 10, node, ffmpeg, yt-dlp.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
data="$(mktemp -d)"
cp "$here/data/yt.json" "$data/"
head -c 62914560 /dev/urandom > "$data/file.bin"
head -c 3145728 /dev/urandom > "$data/small.bin"
ffmpeg -loglevel error -y -f lavfi -i testsrc=size=640x360:rate=25 -f lavfi -i sine=frequency=440 -t 5 -c:v libx264 -c:a aac -shortest "$data/clip.mp4"
port=18080
node "$here/server.js" $port "$data" & server=$!
trap 'kill $server 2>/dev/null; rm -rf "$data"' EXIT
sleep 1
dotnet run -c Release --project "$here/IbdmPro.Tests" -- $port "$data"
