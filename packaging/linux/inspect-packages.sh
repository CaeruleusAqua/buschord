#!/bin/sh
set -eu
if [ "$#" -lt 3 ] || [ "$#" -gt 4 ]; then echo 'usage: inspect-packages.sh <AppImage> <deb> <scratch-output-directory> [self-contained-script-worker-probe]' >&2; exit 2; fi
image=$(realpath "$1"); deb=$(realpath "$2"); scratch=$(realpath -m "$3")
probe=${4:-}
if [ -n "$probe" ]; then probe=$(realpath "$probe"); test -x "$probe"; fi
mkdir -p "$scratch"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT HUP INT TERM
dpkg-deb --info "$deb" >"$scratch/deb-info.txt"
dpkg-deb --contents "$deb" >"$scratch/deb-files.txt"
dpkg-deb --extract "$deb" "$work/deb"
test -f "$work/deb/opt/buschord/.package-managed"
if find "$work/deb" -type f -perm /022 | grep .; then echo 'World/group-writable package files' >&2; exit 1; fi
if find "$work/deb" -type f \( -name '*.pdb' -o -name '*.knxproj' -o -name '*.knxprod' -o -name '*.pfx' -o -name '*.key' \) | grep .; then exit 1; fi
chmod +x "$image"
cd "$work"
"$image" --appimage-extract >"$scratch/appimage-files.txt"
test -f squashfs-root/AppRun
if [ -d squashfs-root/usr/bin/ScriptWorker ] || [ -d "$work/deb/opt/buschord/ScriptWorker" ]; then
 test -n "$probe" || { echo 'Real worker execution requires the self-contained package probe.' >&2; exit 1; }
 # Do not repair permissions here: a mounted AppImage is read-only. Test the packaged modes.
 for application in "$work/squashfs-root/usr/bin" "$work/deb/opt/buschord"; do
  test -x "$application/ScriptWorker/Knx.Scripting"
  "$probe" "$application" >>"$scratch/script-worker-execution.txt" 2>&1
 done
fi
find squashfs-root -type f -name '*.so' -exec sh -c 'echo "$1"; ldd "$1"' sh {} \; >"$scratch/native-dependencies.txt" 2>&1 || true
echo 'Package structure inspection passed. This does not install packages or start the GUI.'
