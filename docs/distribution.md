# Distribution and updates

Windows uses a Velopack per-user setup and portable ZIP. Linux has an AppImage
and a package-managed deb. No release is published by the package workflow.
Production requires reviewed public update keys/feed compiled into both app and
broker, plus Windows publisher signing. Development packages remain unsigned and
updates disabled. Never distribute development keys as production trust anchors.

Local functional packages can pass
`-UpdateTrustSource packaging/trust/ProductionUpdateTrust.g.cs` with the reviewed
production public anchor while retaining an unsigned local installer. Normal
development builds keep the unconfigured source. Public production promotion
additionally requires Windows code signing.

## Build

Use .NET 10.0.403 and pinned Velopack 1.2.161 (`dotnet tool restore`).

```powershell
./packaging/Build-Packages.ps1 -Project ./src/Knx.App/Knx.App.csproj `
  -Version 1.0.0 -Channel stable `
  -Icon ./src/Knx.App/Assets/brand/buschord-house-transparent-tile-256.png `
  -WindowsIcon ./src/Knx.App/Assets/brand/buschord-house-transparent-tile.ico
```

All publish/intermediate outputs go to `artifacts/`. CI builds set deterministic
compilation, ContinuousIntegrationBuild, SourceLink/repository metadata and
PathMap. No PDBs, project archives, catalogues, settings or private signing keys
belong in packages. Release code should have complete third-party notices and
the application licence. Package inspection is required before publication.

The packaged `ScriptWorker/` contains a separate self-contained .NET executable
and runtime. Script evaluation launches this fixed local apphost; it does not
require a system `dotnet` installation. Incomplete packaged workers fail closed.
Framework-dependent development runs retain the current runtime's absolute host.
The package build executes synthetic scalar/function/resource-limit checks
against the extracted Windows portable. Linux inspection executes the same checks
against both extracted AppImage and deb, including their original executable bits:

```sh
sh packaging/linux/inspect-packages.sh artifacts/releases/linux-x64/BusChord-linux-x64-preview.AppImage artifacts/releases/linux-x64/BusChord_amd64.deb artifacts/inspection artifacts/worker-probe/linux-x64/BusChord.ScriptWorkerProbe
```

The probe is a self-contained build-only tool outside the distributed application.
It removes the host search path, rejects framework-dependent worker metadata and
never starts the GUI or connects to a bus. Minimal Linux test images can explicitly
set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`; that verifies worker execution,
not localized desktop behaviour or the native GUI dependencies.

For `-Production`, set reviewed **public** configuration:
`BUSCHORD_UPDATE_PUBLIC_KEY_ID`, `BUSCHORD_UPDATE_PUBLIC_KEY_PEM`,
`BUSCHORD_UPDATE_FEED_BASE`, plus the protected Windows signing command in
`BUSCHORD_SIGN_TEMPLATE`. The build compiles public trust into both app and
broker. The default source has no key/feed and makes no network requests.

## Sign and publish metadata

Generate a P-256 signing key outside the checkout. Keep its private part in a
protected signing environment; only the public key is a build input.

```powershell
dotnet run --project tools-src/BusChord.Release -- keygen C:/private/buschord-update-private.pem C:/private/buschord-update-public.pem
dotnet run --project tools-src/BusChord.Release -- sign artifacts/releases/win-x64/BusChord-1.0.0-win-x64-stable-full.nupkg 1.0.0 win-x64 stable 1793145600 https://github.com/OWNER/REPOSITORY/releases/download/v1.0.0/ production-2026 C:/private/buschord-update-private.pem artifacts/updates.win-x64.stable.json
```

Use a monotonically increasing sequence per RID/channel, for example UTC Unix
seconds under a serialized protected release job. Sign feeds for every target.
Use `env:BUSCHORD_UPDATE_SIGNING_KEY` instead of a key file in CI. The tool never
prints key material. Metadata expires after seven days; refresh unchanged
releases before expiry. The feed base can be a stable static HTTPS directory or
public GitHub `releases/latest/download/` once signed production assets exist.
Publish exact matching `updates.<rid>.<channel>.json` assets. Preview uses its own
explicit feed directory/channel; a stable install never opts into previews.

The envelope signs exact payload bytes using BCL ECDSA P-256/SHA-256. It binds
package SHA-256, size, filename, app ID, version, RID, channel, sequence and
expiry. Package nuspec scope is checked before signing and before application.
Trust keys are never downloaded. For rotation, first ship a release containing
both reviewed public keys signed under the old key; only then change signers.
Compromised-root recovery requires an independently trusted installer channel.

## Application lifecycle

Call `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` first. The app holds
`UpdateProcessLease.EnterApplication(stateDirectory)` throughout its UI lifetime.
Use a state directory outside the installation, such as
`LocalApplicationData/BusChord.UpdateState`, never `LocalAppData/BusChord/updates`.
Use `EnterPackageManagedApplication()` for `/opt/buschord` installations.

Default policy checks/downloads automatically, and applies before UI startup or
after normal close has passed the existing unsaved-project/parameter-draft gate.
No per-version confirmation, forced close or automatic editor restart is needed.
Automatic/CheckOnly/Disabled and Stable/Preview are global user preferences;
editable settings cannot alter publisher keys or feed origins. On normal close,
release the instance lease, start the broker, then return normally. On startup,
if a broker starts an update, return; it starts the new app afterwards. App starts
with the internal --skip-auto-update-once recovery argument skip startup apply
once, even when a full disk prevented writing the failure ledger. They still
verify every future package normally. Public GitHub preview checks discover the
latest non-draft prerelease's correctly named signed feed asset; stable checks
use the configured latest-release feed base. Every app start
must retry a busy intent lease before opening any window. No active instance may
bypass this lease. The no-kill guarantee covers these protected app instances.

The external broker and its native updater inherit kernel leases. Killing the
broker therefore does not permit another editor to start mid-swap. The broker
never times out and kills an editor. Failed package hashes are recorded and
suppressed on subsequent starts until a new package or explicit retry.

Windows also checks the complete published code inventory before and after the
native swap: every payload file hash and expected directory, no links or extra
user files, plus the inventory self-checksum and exact package scope. The only
SDK-created file, `sq.version`, has bounded XML scope validation. Unknown files,
even without recognized project extensions, defer the update without deletion.
Code signing happens before inventory generation; the final portable payload is
checked against that inventory before an artifact may be distributed. Failed
package hashes keep the `retry-required` status until the user explicitly calls
`RetryPendingUpdateAsync`; routine checks never silently reset it.

On Linux, the broker stages inside the AppImage's own directory, flushes bytes,
keeps a hard-linked `.previous`, and performs one same-filesystem rename. It
never invokes the SDK's cross-device `mv`/`pkexec` fallback. A read-only location
fails safely; install the AppImage in a user-owned directory for automatic updates.
On Windows, native apply runs only under the exclusive lease; before it starts,
an independent, hash-verified copy of `current` is kept outside the installation.
Missing-current/nonzero outcomes restore it. Recovery metadata/backups remain for
diagnosis and recovery after a combined broker/updater failure. Power-loss and
native Windows rollback failure testing remain release acceptance work.

## Linux requirements and APT

Supported package baselines: Ubuntu 22.04/24.04/26.04 and Debian 12/13 x64, with
glibc 2.35+, .NET 10 native dependencies, X11/XWayland, fontconfig/fonts, DBus and
OpenGL. Run `sh packaging/linux/check-runtime.sh` for an explicit dependency
check. Self-contained .NET does not remove native-library requirements. FUSE is
needed to mount AppImages; extraction mode is a fallback for inspection. The deb
declares OS library dependencies; minimal WSL images can lack ICU and a display.

The deb never overwrites itself. An optional one-time administrator setup installs
one reviewed repository signing key and enables `buschord-update.timer`:

```sh
sudo sh packaging/linux/configure-apt.sh https://YOUR-APT-HOST/buschord reviewed-public-key.asc REVIEWED_PRIMARY_FINGERPRINT
```

Production deb builds can instead set BUSCHORD_APT_REPOSITORY,
BUSCHORD_APT_PUBLIC_KEY_FILE and BUSCHORD_APT_FINGERPRINT. The same validated
public configuration is included in the package and activated at first install,
using that installation's administrator permission. No later setup prompt is
needed. `install-appimage-user.sh` optionally copies an already downloaded image
with its published checksum to a user-owned directory and creates a desktop entry.

The service uses isolated sources/lists/cache, recent signed Release metadata,
`--only-upgrade --no-remove`, a shared app-running lease, and a Version-2 APT
pre-dpkg transaction hook. It rejects foreign unpack/configure/remove actions
even if APT resolves dependencies differently after simulation. Dependency changes
wait for manual administration. Package triggers can run as part of ordinary
dpkg lifecycle; they are distinct from upgrading another package. No broad OS
upgrade, untrusted repository exception or repeated administrator prompt is used.
Disable with `sudo systemctl disable --now buschord-update.timer` and remove
`/etc/buschord-updater/enabled`. APT policy remains administrator-owned.

Those `/etc` files are dpkg conffiles, so administrator edits/deletions persist
across upgrades. The package enables its timer only at first install. Network
requests and lock waits are bounded; the service has no commit timeout that can
kill a running dpkg transaction.

The guarded commit permits configuration of the own package, including an already
unpacked installation. It passes dpkg's `--force-confdef --force-confold` to retain
administrator conffile changes without an unattended question; debconf's
`DEBIAN_FRONTEND=noninteractive` alone does not answer dpkg conffile questions.
An APT exit code of zero is accepted only when BusChord's complete dpkg status is
`install ok installed`. The real transaction hook still rejects foreign pending
configure actions before dpkg executes them.

`apt-repository.yml` verifies an existing ECDSA-signed stable Linux full package,
extracts its authenticated AppImage, builds the deb and GPG-signed repository,
then deploys `/apt/` through GitHub Pages. It never accepts an arbitrary release
deb for blind signing. `BUSCHORD_APT_PUBLISH_ENABLED` defaults off and must only
be enabled after an approved production release. Configure GitHub Pages to use
Actions. Public APT variables are `BUSCHORD_APT_REPOSITORY`,
`BUSCHORD_APT_FINGERPRINT` and `BUSCHORD_APT_PUBLIC_KEY_ASC`; the encrypted
signing secret is `BUSCHORD_APT_SIGNING_KEY`, with optional
`BUSCHORD_APT_SIGNING_PASSPHRASE`. Neither workflow imports private keys into the
checkout or packages. Scheduled metadata refresh remains publication-gated.

Build signed APT metadata using `build-apt-repository.sh` with an existing external
GPG signing key. It requires `dpkg-scanpackages`, `apt-ftparchive` and GnuPG on the
release builder. No repository is added automatically from an app-supplied URL.

## Verification and current boundaries

`dotnet run --project tests/Knx.Updates.Tests -c Release` tests signatures, scope,
anti-rollback, cache/staging tamper, cross-process leases, broker crash and code
backup recovery with synthetic payloads. Linux adds same-filesystem AppImage
replacement and injected disk/permission/interruption failures. Run
`python3 packaging/linux/test-transaction-hook.py` for APT action-scope fixtures.
`python3 packaging/linux/test-updater-contract.py` executes the product shell with
temporary paths, real `flock`/`awk` and an emulated APT/dpkg boundary. It checks
own-package configure/poststatus, conffile conflicts, incomplete status after
APT success, app leases and foreign-dependency deferral. It also invokes the real
V2 hook for own/foreign configure-only streams. No package manager or service is
run by this test; it complements installed acceptance rather than replacing it.
`inspect-packages.sh` extracts to a private temporary directory; it does not install
or launch a GUI. Synthetic probe builds prove packaging, not BusChord GUI acceptance.

The complete BusChord 0.1.0-preview.38 application has also been packaged locally
as Windows setup/portable ZIP and Linux AppImage/deb. The portable package's code
inventory passes; its self-contained worker passes scalar, prepared-function,
timeout and recovery checks without a discoverable global .NET host. Both extracted
Linux packages pass the same four worker checks and preserve executable modes.
The local Linux worker run used explicit invariant globalization in a minimal
ICU-free WSL image; this does not accept the localized Linux GUI. Actual product
update packages were signed and verified locally against the reviewed public
anchor. No installer or feed was published and no package was installed on the host.

Windows update regressions pass 54 checks. The independent Linux run passes 59;
APT source/transaction fixtures pass with Python optimization levels 0, 1 and 2.
Native dialogs pass 854 checks each in German and English, including update
preferences and asynchronous session shutdown before releasing the application
lease. Full desktop-platform and power-loss acceptance remain release work.

The 2026-10-29 Ubuntu 26.04/WSLg run also started the native self-contained
application, passed smoke and screenshot checks, and passed the Workbench's
German/light and English/dark native workflows. It used isolated XDG/settings
directories and privately extracted ICU 78, libICE and libSM libraries, without
invariant globalization or host package installation. This verifies that published
payload in one Linux desktop environment. A separate dedicated Ubuntu 26.04/APT
3.2 installation exposed incomplete own-package configuration and unattended
conffile failure. The corrected installed service completed signed policy fixtures
with the full preview.41 payload, preserved administrator sources and timer
disable/deletion policy, and deferred app-running and foreign-dependency cases.
Actual APT V2 streams and frontend-lock checks also rejected foreign pending
configure actions. These synthetic 41.x package-policy versions reuse application
bytes; they are not new application releases. A complete preview.42 application
upgrade with the corrected package, other supported platforms/APT versions,
AppImage installation and power-loss acceptance remain open. Private acceptance
keys, package fixtures and logs stay outside the repository.

Primary documentation checked 2026-10-28:
- [Velopack packaging](https://docs.velopack.io/packaging/overview),
  [Windows](https://docs.velopack.io/packaging/operating-systems/windows),
  [Linux](https://docs.velopack.io/packaging/operating-systems/linux),
  [integration](https://docs.velopack.io/integrating/overview).
- [Pinned SDK CLI contract](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/src/lib-csharp/UpdateExe.cs).
- [APT authentication](https://manpages.debian.org/trixie/apt/apt-secure.8.en.html),
  [APT transaction hook protocol](https://manpages.debian.org/bookworm/apt/apt.conf.5.en.html#HOW_APT_CALLS_DPKG(1)),
  [dpkg package states and conffile options](https://manpages.debian.org/trixie/dpkg/dpkg.1.en.html).
- [Avalonia Linux deployment](https://docs.avaloniaui.net/docs/deployment/linux),
  [.NET 10 native dependencies](https://github.com/dotnet/core/blob/main/release-notes/10.0/dotnet-dependencies.md).
