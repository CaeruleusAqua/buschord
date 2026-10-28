#!/bin/sh
set -eu
if [ "$#" -lt 4 ] || [ "$#" -gt 6 ]; then echo 'usage: build-deb.sh <published-linux-directory> <version> <icon.png> <output.deb> [main-executable] [package-name]' >&2; exit 2; fi
publish=$(realpath "$1"); version=$(printf '%s' "$2" | sed 's/-/~/;s/+.*$//'); icon=$(realpath "$3"); output=$(realpath -m "$4")
main=${5:-BusChord}; packageid=${6:-buschord}
printf '%s\n' "$main" | grep -Eq '^[A-Za-z0-9._-]+$' || exit 2
printf '%s\n' "$packageid" | grep -Eq '^[a-z0-9][a-z0-9+.-]+$' || exit 2
dpkg --validate-version "$version"
script=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ "$packageid" = buschord ] && [ -z "${BUSCHORD_APT_REPOSITORY:-}${BUSCHORD_APT_PUBLIC_KEY_FILE:-}${BUSCHORD_APT_FINGERPRINT:-}" ] && [ -f "$script/../trust/apt-production.json" ]; then
 BUSCHORD_APT_REPOSITORY=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["Repository"])' "$script/../trust/apt-production.json")
 BUSCHORD_APT_FINGERPRINT=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["Fingerprint"])' "$script/../trust/apt-production.json")
 BUSCHORD_APT_PUBLIC_KEY_FILE="$script/../trust/apt-production-2026.public.asc"
fi
root=$(mktemp -d)
trap 'rm -rf -- "$root"' EXIT HUP INT TERM
install -d "$root/DEBIAN" "$root/opt/buschord" "$root/usr/bin" "$root/usr/share/applications" \
  "$root/usr/share/icons/hicolor/256x256/apps" "$root/usr/share/doc/buschord" \
  "$root/usr/lib/buschord-update" "$root/usr/lib/systemd/system" "$root/etc/buschord-updater"
cp -a "$publish/." "$root/opt/buschord/"
find "$root/opt/buschord" -type f -exec chmod 0644 {} +
touch "$root/opt/buschord/.package-managed"
chmod 0755 "$root/opt/buschord/$main"
if [ -f "$root/opt/buschord/createdump" ]; then chmod 0755 "$root/opt/buschord/createdump"; fi
for executable in ScriptWorker/Knx.Scripting ScriptWorker/createdump UpdateBroker/BusChord.UpdateBroker UpdateBroker/createdump; do
 if [ -f "$root/opt/buschord/$executable" ]; then chmod 0755 "$root/opt/buschord/$executable"; fi
done
ln -s "/opt/buschord/$main" "$root/usr/bin/$packageid"
sed "s@Exec=/opt/buschord/BusChord@Exec=/opt/buschord/$main@" "$script/BusChord.desktop" >"$root/usr/share/applications/BusChord.desktop"
install -m0644 "$icon" "$root/usr/share/icons/hicolor/256x256/apps/buschord.png"
install -m0755 "$script/update-buschord" "$root/usr/lib/buschord-update/"
install -m0755 "$script/verify-transaction" "$root/usr/lib/buschord-update/"
install -m0644 "$script/buschord-update.service" "$script/buschord-update.timer" "$root/usr/lib/systemd/system/"
if [ -n "${BUSCHORD_APT_REPOSITORY:-}${BUSCHORD_APT_PUBLIC_KEY_FILE:-}${BUSCHORD_APT_FINGERPRINT:-}" ]; then
 : "${BUSCHORD_APT_REPOSITORY:?}" "${BUSCHORD_APT_PUBLIC_KEY_FILE:?}" "${BUSCHORD_APT_FINGERPRINT:?}"
 python3 "$script/stage-apt-source.py" "$BUSCHORD_APT_REPOSITORY" "$BUSCHORD_APT_PUBLIC_KEY_FILE" "$BUSCHORD_APT_FINGERPRINT" "$root"
fi
if [ -f "$root/etc/buschord-updater/buschord.sources" ]; then
 printf '%s\n' /etc/buschord-updater/buschord.sources /etc/buschord-updater/enabled >"$root/DEBIAN/conffiles"
fi
cat >"$root/DEBIAN/control" <<EOF
Package: $packageid
Version: $version
Section: electronics
Priority: optional
Architecture: amd64
Maintainer: BusChord contributors
Depends: ca-certificates, tzdata, libc6 (>= 2.35), libgcc-s1, libgssapi-krb5-2, libstdc++6, libx11-6, libice6, libsm6, libfontconfig1, libfreetype6, libdbus-1-3, libgl1, libicu70 | libicu72 | libicu74 | libicu76 | libicu78, libssl3 | libssl3t64, zlib1g, procps, util-linux, python3-minimal
Description: Offline building automation project workbench
 Native .NET/Avalonia workbench. Package-managed installations use APT updates.
EOF
cat >"$root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
install -d -m0755 /var/lib/buschord-updater
touch /var/lib/buschord-updater/running.lock
chmod 0644 /var/lib/buschord-updater/running.lock
if command -v systemctl >/dev/null 2>&1; then
 systemctl daemon-reload || true
 # Activation requires a separately reviewed repository/key setup; never add trust from the network.
 if [ "$1" = configure ] && [ -z "${2:-}" ] && [ -f /etc/buschord-updater/enabled ] && [ -f /etc/buschord-updater/buschord.sources ]; then
  systemctl enable --now buschord-update.timer || true
 fi
fi
EOF
cat >"$root/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = remove ] && command -v systemctl >/dev/null 2>&1; then
 systemctl disable --now buschord-update.timer || true
fi
EOF
chmod 0755 "$root/DEBIAN/postinst" "$root/DEBIAN/prerm"
find "$root" -type d -exec chmod 0755 {} +
mkdir -p "$(dirname "$output")"
dpkg-deb --build --root-owner-group "$root" "$output"
