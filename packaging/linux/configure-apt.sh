#!/bin/sh
set -eu
# One-time administrator setup. No repository or key is fetched from a running app.
if [ "$#" -ne 3 ] || [ "$(id -u)" -ne 0 ]; then
 echo 'usage as root: configure-apt.sh <https-repository-url> <reviewed-public-key.asc> <reviewed-primary-fingerprint>' >&2; exit 2
fi
script=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
python3 "$script/stage-apt-source.py" "$1" "$2" "$3" /
systemctl daemon-reload
systemctl enable --now buschord-update.timer
echo 'Automatic authenticated BusChord-only updates enabled. Disable with: systemctl disable --now buschord-update.timer'
