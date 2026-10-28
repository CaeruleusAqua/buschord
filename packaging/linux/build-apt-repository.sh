#!/bin/sh
set -eu
if [ "$#" -ne 3 ]; then echo 'usage: build-apt-repository.sh <package.deb> <output-directory> <existing-gpg-signing-key-id>' >&2; exit 2; fi
package=$(realpath "$1"); destination=$(realpath -m "$2"); key=$3
mkdir -p "$destination/pool/main" "$destination/dists/stable/main/binary-amd64"
cp "$package" "$destination/pool/main/"
cd "$destination"
dpkg-scanpackages --arch amd64 pool/main /dev/null >dists/stable/main/binary-amd64/Packages
gzip -n -k -f dists/stable/main/binary-amd64/Packages
apt-ftparchive -o APT::FTPArchive::Release::Origin=BusChord \
 -o APT::FTPArchive::Release::Label=BusChord -o APT::FTPArchive::Release::Suite=stable \
 -o APT::FTPArchive::Release::Codename=stable -o APT::FTPArchive::Release::Architectures=amd64 \
 -o APT::FTPArchive::Release::Components=main release dists/stable >dists/stable/Release
printf 'Valid-Until: %s\n' "$(date -u -d '+7 days' -R)" >>dists/stable/Release
if [ -n "${BUSCHORD_APT_SIGNING_PASSPHRASE:-}" ]; then
 printf '%s' "$BUSCHORD_APT_SIGNING_PASSPHRASE" | gpg --batch --yes --pinentry-mode loopback --passphrase-fd 0 --local-user "$key" --digest-algo SHA256 --clearsign -o dists/stable/InRelease dists/stable/Release
 printf '%s' "$BUSCHORD_APT_SIGNING_PASSPHRASE" | gpg --batch --yes --pinentry-mode loopback --passphrase-fd 0 --local-user "$key" --digest-algo SHA256 --armor --detach-sign -o dists/stable/Release.gpg dists/stable/Release
else
 gpg --batch --yes --local-user "$key" --digest-algo SHA256 --clearsign -o dists/stable/InRelease dists/stable/Release
 gpg --batch --yes --local-user "$key" --digest-algo SHA256 --armor --detach-sign -o dists/stable/Release.gpg dists/stable/Release
fi
