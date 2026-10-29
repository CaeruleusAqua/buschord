#!/usr/bin/env python3
import os, pathlib, subprocess, tempfile

script=pathlib.Path(__file__).with_name('verify-transaction')
checks=0
with tempfile.TemporaryDirectory(prefix='buschord-apt-hook-checks-') as directory:
    root=pathlib.Path(directory)
    def package(name):
        folder=root/name; (folder/'DEBIAN').mkdir(parents=True)
        (folder/'DEBIAN/control').write_text(f'Package: {name}\nVersion: 2.0\nArchitecture: all\nMaintainer: Fixture <fixture@example.test>\nDescription: Synthetic package, never installed\n')
        target=root/(name+'.deb')
        subprocess.run(['dpkg-deb','--build','--root-owner-group',str(folder),str(target)],check=True,stdout=subprocess.DEVNULL)
        return target
    own=package('buschord'); foreign=package('review-lib')
    def check(name, actions, accepted=False, header='VERSION 2\n\n', info=True):
        global checks
        env=os.environ.copy()
        if info: env['APT_HOOK_INFO_FD']='0'
        else: env.pop('APT_HOOK_INFO_FD',None)
        result=subprocess.run(['python3',str(script)],input=header+actions,text=True,env=env,capture_output=True)
        okay=(result.returncode==0)==accepted
        print(('PASS ' if okay else 'FAIL ')+name)
        if not okay: raise RuntimeError(name)
        checks+=1
    check('BusChord-only real upgrade and configure',f'buschord 1.0 < 2.0 {own}\nbuschord 2.0 = 2.0 **CONFIGURE**\n',True)
    check('BusChord already unpacked configure-only transaction','buschord 2.0 = 2.0 **CONFIGURE**\n',True)
    check('foreign dependency unpack rejected',f'review-lib 1.0 < 2.0 {foreign}\nbuschord 1.0 < 2.0 {own}\n')
    check('foreign configure rejected','review-lib 2.0 = 2.0 **CONFIGURE**\n')
    check('foreign bytes under own action rejected',f'buschord 1.0 < 2.0 {foreign}\n')
    check('remove rejected','buschord 1.0 > - **REMOVE**\n')
    check('downgrade rejected',f'buschord 3.0 > 2.0 {own}\n')
    check('first install rejected',f'buschord - < 2.0 {own}\n')
    check('unknown protocol rejected','',header='VERSION 3\n\n')
    check('missing information descriptor rejected','',info=False)
    check('empty transaction rejected','')
print(f'{checks} checks passed; no package was installed.')
