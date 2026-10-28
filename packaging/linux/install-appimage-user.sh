#!/bin/sh
set -eu
if [ "$#" -lt 2 ] || [ "$#" -gt 3 ]; then echo 'usage: install-appimage-user.sh <reviewed-local.AppImage> <published-sha256> [icon.png]' >&2; exit 2; fi
source=$(realpath "$1"); hash=$2
case "$hash" in ''|*[!0-9a-fA-F]*) echo 'A published SHA-256 checksum is required' >&2; exit 2 ;; esac
[ "${#hash}" -eq 64 ] || exit 2
printf '%s  %s\n' "$hash" "$source" | sha256sum --check --status - || { echo 'The downloaded AppImage checksum does not match' >&2; exit 1; }
data=${XDG_DATA_HOME:-$HOME/.local/share}
app="$data/buschord"; state="$data/BusChord.UpdateState"
install -d -m0755 "$app" "$state" "$data/applications" "$data/icons/hicolor/256x256/apps"
exec 9>"$state/intent.lock"; flock -x -n 9 || { echo 'An update is already active' >&2; exit 1; }
exec 8>"$state/running.lock"; flock -x -n 8 || { echo 'Close BusChord before replacing its AppImage' >&2; exit 1; }
stage=$(mktemp "$app/.BusChord-stage-XXXXXX")
trap 'rm -f -- "$stage"' EXIT HUP INT TERM
cp "$source" "$stage"; chmod 0755 "$stage"; mv -f "$stage" "$app/BusChord.AppImage"
if [ "$#" -eq 3 ]; then install -m0644 "$3" "$data/icons/hicolor/256x256/apps/buschord.png"; fi
# Desktop Exec is parsed by the desktop, not a shell. Escape its two string metacharacters.
executable=$(printf '%s' "$app/BusChord.AppImage" | sed 's/\\/\\\\/g;s/"/\\"/g;s/%/%%/g')
cat >"$data/applications/buschord.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=BusChord
Comment=Building automation project workbench
Exec="$executable" --open=%f
Icon=buschord
Terminal=false
Categories=Development;Engineering;
EOF
chmod 0644 "$data/applications/buschord.desktop"
echo 'BusChord is installed in your user directory. No administrator rights or running app restart were used.'
