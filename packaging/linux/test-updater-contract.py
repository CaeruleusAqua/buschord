#!/usr/bin/env python3
"""Run the real updater shell with temporary paths and an emulated APT/dpkg boundary.

No package manager or system service is invoked. The model covers configure-pending,
conffile decisions and package status, not real APT resolution/signatures/locking.
See docs/distribution.md for the separate installed APT 3.2 acceptance evidence.
"""
import fcntl
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name('update-buschord')
if len(sys.argv) > 1 and sys.argv[1] == '--script':
    SCRIPT = pathlib.Path(sys.argv[2]).resolve()
    del sys.argv[1:3]

# Executed only through the fixture's closed PATH. Unknown commands fail, rather
# than falling through to the machine's apt-get, dpkg-query, pgrep or install.
COMMAND = r'''
import json, os, pathlib, subprocess, sys
root = pathlib.Path(os.environ['UPDATER_FIXTURE'])
state_path = root / 'state.json'
state = json.loads(state_path.read_text())
name = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
def save(): state_path.write_text(json.dumps(state))
def end(code=0):
    save()
    sys.exit(code)
if name == 'pgrep':
    state['pgrep_calls'] += 1
    end(0 if state['running_at'] == state['pgrep_calls'] else 1)
if name == 'install':
    if args[:3] != ['-d', '-m', '0755']: end(91)
    for arg in args[3:]:
        path = pathlib.Path(arg)
        if not path.is_relative_to(root): end(92)
        path.mkdir(parents=True, exist_ok=True)
    end()
if name == 'dpkg-query':
    if args != ['-W', '-f=${Status}', 'buschord']: end(93)
    state['queries'] += 1
    if state['query_failure']: end(1)
    print(state['status'], end='')
    end()
if name != 'apt-get': end(94)
options, operands = {}, []
i = 0
while i < len(args):
    if args[i] == '-o':
        key, value = args[i+1].split('=', 1)
        options.setdefault(key, []).append(value)
        i += 2
    else:
        operands.append(args[i])
        i += 1
phase = 'update' if operands == ['update'] else 'simulate' if '--simulate' in operands else 'commit'
state['calls'].append(phase)
if phase == 'update': end()
if operands[-2:] != ['install', 'buschord']: end(95)
if phase == 'simulate':
    print(state['simulation'])
    end()
if state['commit_failure']: end(100)
configure = options.get('DPkg::ConfigurePending', ['true'])[-1].lower() != 'false'
if configure:
    # Run the PRODUCT hook with a real V2 configure-only stream, also covering a
    # foreign pending configure that appeared after a clean simulation.
    hook = options.get('DPkg::Pre-Install-Pkgs::', [])
    if len(hook) != 1: end(96)
    if options.get('DPkg::Tools::Options::'+hook[0]+'::Version') != ['2']: end(97)
    if options.get('DPkg::Tools::Options::'+hook[0]+'::InfoFD') != ['0']: end(98)
    env = dict(os.environ, APT_HOOK_INFO_FD='0')
    actions = 'buschord 2.0 = 2.0 **CONFIGURE**\n'
    if state['foreign_pending']: actions += 'review-lib 1.0 = 1.0 **CONFIGURE**\n'
    result = subprocess.run([sys.executable, hook[0]], input='VERSION 2\n\n'+actions,
                            text=True, env=env, capture_output=True)
    state['hook_calls'] += 1
    if result.returncode: end(100)
state['status'] = 'install ok unpacked'
if configure and not state['false_success']:
    if state['conflict']:
        force = options.get('DPkg::Options::', [])
        source = root / 'etc/buschord.sources'
        if '--force-confold' in force:
            source.with_suffix('.sources.dpkg-dist').write_text('new package default\n')
        elif '--force-confnew' in force:
            source.write_text('new package default\n')
        else:
            # DEBIAN_FRONTEND controls debconf, not this dpkg conffile question.
            state['prompts'] += 1
            print('dpkg: end of file on stdin at conffile prompt', file=sys.stderr)
            end(100)
    state['configured'] += 1
    state['status'] = 'install ok installed'
if state['false_success']: state['status'] = state['incomplete_status']
end()
'''


class UpdaterContract(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='buschord-updater-contract-')
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        for folder in ('bin', 'lock', 'state', 'etc', 'lib'):
            (self.root / folder).mkdir()
        (self.root / 'state/running.lock').touch()
        (self.root / 'etc/buschord.sources').write_text('administrator source\n')
        hook = pathlib.Path(__file__).with_name('verify-transaction')
        shutil.copyfile(hook, self.root / 'lib/verify-transaction')
        source = SCRIPT.read_text()
        for original, replacement in (
            ('/run/lock/buschord-update.lock', 'lock/update.lock'),
            ('/var/lib/buschord-updater', 'state'),
            ('/var/cache/buschord-updater', 'cache'),
            ('/etc/buschord-updater', 'etc'),
            ('/usr/lib/buschord-update', 'lib'),
        ):
            source = source.replace(original, str(self.root / replacement))
        (self.root / 'updater').write_text(source)
        for command in ('apt-get', 'dpkg-query', 'pgrep', 'install'):
            path = self.root / 'bin' / command
            path.write_text('#!' + sys.executable + '\n' + COMMAND)
            path.chmod(0o755)
        for command in ('awk', 'flock'):
            target = shutil.which(command)
            if target is None: self.fail('Required shell utility unavailable: ' + command)
            (self.root / 'bin' / command).symlink_to(target)

    def run_updater(self, hold_running_lease=False, **changes):
        state = dict(status='install ok installed', calls=[], pgrep_calls=0, running_at=0,
                     queries=0, configured=0, prompts=0, hook_calls=0, foreign_pending=False,
                     conflict=False, false_success=False, commit_failure=False, query_failure=False,
                     incomplete_status='install ok unpacked',
                     simulation='Inst buschord [1.0] (2.0 synthetic [all])')
        state.update(changes)
        path = self.root / 'state.json'
        path.write_text(json.dumps(state))
        env = dict(os.environ, PATH=str(self.root / 'bin'), UPDATER_FIXTURE=str(self.root), LC_ALL='C')
        with (self.root / 'state/running.lock').open() as lease:
            if hold_running_lease: fcntl.flock(lease, fcntl.LOCK_SH)
            result = subprocess.run(['/bin/sh', str(self.root / 'updater')], env=env,
                                    stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=10)
        return result, json.loads(path.read_text())

    def test_own_configure_only_completes_and_checks_status(self):
        result, state = self.run_updater(status='install ok unpacked', simulation='Conf buschord (2.0)')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((state['status'], state['configured'], state['queries']), ('install ok installed', 1, 1))
        self.assertEqual(state['hook_calls'], 1)

    def test_changed_admin_and_package_source_keep_admin_without_prompt(self):
        result, state = self.run_updater(conflict=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((state['status'], state['configured'], state['prompts']), ('install ok installed', 1, 0))
        self.assertEqual((self.root / 'etc/buschord.sources').read_text(), 'administrator source\n')
        self.assertEqual((self.root / 'etc/buschord.sources.dpkg-dist').read_text(), 'new package default\n')

    def test_successful_apt_requires_complete_package_status(self):
        for status in ('install ok unpacked', 'install ok half-configured',
                       'deinstall ok installed', 'install reinstreq installed'):
            with self.subTest(status=status):
                result, state = self.run_updater(false_success=True, incomplete_status=status)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual((state['status'], state['queries']), (status, 1))

    def test_running_app_defers_before_and_after_simulation(self):
        for running_at, calls in ((1, []), (2, ['update', 'simulate'])):
            with self.subTest(running_at=running_at):
                result, state = self.run_updater(running_at=running_at)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(state['calls'], calls)
                self.assertEqual((state['configured'], state['queries']), (0, 0))

    def test_running_application_shared_lease_defers(self):
        result, state = self.run_updater(hold_running_lease=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(state['calls'], [])

    def test_new_and_upgraded_foreign_dependencies_defer(self):
        for foreign in ('Inst review-lib (1.0)', 'Inst review-lib [1.0] (2.0)', 'Remv review-lib [1.0]'):
            with self.subTest(action=foreign):
                result, state = self.run_updater(simulation=foreign+'\nInst buschord [1.0] (2.0)')
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(state['calls'], ['update', 'simulate'])
                self.assertEqual((state['status'], state['configured']), ('install ok installed', 0))

    def test_foreign_pending_configure_is_still_rejected_by_real_hook(self):
        result, state = self.run_updater(foreign_pending=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((state['hook_calls'], state['configured']), (1, 0))
        self.assertEqual(state['status'], 'install ok installed')

    def test_commit_or_status_query_failure_is_not_success(self):
        for failure in ('commit_failure', 'query_failure'):
            with self.subTest(failure=failure):
                result, state = self.run_updater(**{failure: True})
                self.assertNotEqual(result.returncode, 0)
                if failure == 'commit_failure': self.assertEqual(state['queries'], 0)


if __name__ == '__main__':
    unittest.main(verbosity=2)
