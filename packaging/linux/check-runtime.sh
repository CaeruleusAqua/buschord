#!/bin/sh
set -eu
# Self-contained .NET still requires native OS libraries; do not hide these assumptions.
missing=''
for library in libX11.so.6 libICE.so.6 libSM.so.6 libfontconfig.so.1 libfreetype.so.6 libdbus-1.so.3 libGL.so.1 libgssapi_krb5.so.2 libssl.so.3 libz.so.1; do
 if ! ldconfig -p 2>/dev/null | grep -F "$library " >/dev/null; then missing="$missing $library"; fi
done
if ! ldconfig -p 2>/dev/null | grep -E 'libicuuc\.so\.(70|72|74|76|78) ' >/dev/null; then missing="$missing ICU(70/72/74/76/78)"; fi
if [ -n "$missing" ]; then
 printf 'BusChord needs these native libraries:%s\nSee docs/distribution.md for supported distributions and package names.\n' "$missing" >&2; exit 1
fi
if [ -z "${DISPLAY:-}" ]; then echo 'BusChord requires an X11 display (XWayland is supported). No DISPLAY is set.' >&2; exit 1; fi
if ! command -v fc-list >/dev/null 2>&1 || [ -z "$(fc-list 2>/dev/null | head -n 1)" ]; then
 echo 'BusChord requires an installed fontconfig font set. Install a desktop font package, for example fonts-dejavu-core.' >&2; exit 1
fi
echo 'BusChord native runtime requirements are available.'
