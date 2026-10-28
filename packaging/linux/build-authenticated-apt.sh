#!/bin/sh
set -eu
if [ "$#" -ne 6 ]; then echo 'usage: build-authenticated-apt.sh <signed-feed> <full-nupkg> <public-trust.cs> <site-output> <gpg-key-id> <icon.png>' >&2; exit 2; fi
feed=$(realpath "$1"); package=$(realpath "$2"); trust=$(realpath "$3"); output=$(realpath -m "$4"); key=$5; icon=$(realpath "$6")
script=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
source=$(CDPATH= cd -- "$script/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT HUP INT TERM
dotnet run --project "$source/tools-src/BusChord.Release" "-p:BusChordUpdateTrustSource=$trust" -- verify-release "$feed" "$package" >"$work/verified.json"
version=$(python3 -c 'import json,sys;v=json.load(open(sys.argv[1]));sys.exit("Unsupported authenticated package scope") if not(v["AppId"]=="BusChord" and v["Rid"]=="linux-x64" and v["Channel"]=="stable") else print(v["Version"])' "$work/verified.json")
python3 - "$package" "$work/BusChord.AppImage" <<'PY'
import shutil,sys,zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
 entry=archive.getinfo('lib/app/BusChord.AppImage')
 if not 0 < entry.file_size <= 1024*1024*1024: raise ValueError('Invalid authenticated AppImage size')
 with archive.open(entry) as src,open(sys.argv[2],'xb') as dst: shutil.copyfileobj(src,dst)
PY
chmod 0755 "$work/BusChord.AppImage"
(cd "$work" && ./BusChord.AppImage --appimage-extract >/dev/null)
sh "$script/build-deb.sh" "$work/squashfs-root/usr/bin" "$version" "$icon" "$work/BusChord_amd64.deb"
sh "$script/build-apt-repository.sh" "$work/BusChord_amd64.deb" "$output/apt" "$key"
cp "$script/../trust/apt-production-2026.public.asc" "$output/apt/buschord-archive.asc"
printf '%s\n' 'BusChord authenticated APT repository. Configure its published key fingerprint before use.' >"$output/apt/index.txt"
