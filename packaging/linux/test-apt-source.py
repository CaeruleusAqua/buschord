#!/usr/bin/env python3
import importlib.util, pathlib, subprocess, tempfile

spec=importlib.util.spec_from_file_location('stage_apt_source',pathlib.Path(__file__).with_name('stage-apt-source.py'))
module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
count=0
def check(name, action, expected=False):
    global count
    try: action(); result=True
    except Exception: result=False
    if result != expected: raise RuntimeError(name)
    count+=1; print('PASS '+name)
check('ordinary public HTTPS source accepted',lambda:module.validate_url('https://packages.example.test/buschord'),True)
for name,url in [('newline field injection','https://packages.example.test/\nTrusted: yes'),('HTTP','http://packages.example.test/'),
                 ('credentials','https://user:password@packages.example.test/'),('port','https://packages.example.test:8443/'),
                 ('query','https://packages.example.test/?secret=1'),('IP literal','https://127.0.0.1/')]:
    check(name+' rejected',lambda url=url:module.validate_url(url))
with tempfile.TemporaryDirectory(prefix='buschord-apt-public-key-checks-') as temp:
    root=pathlib.Path(temp); home=root/'private-gnupg'; home.mkdir(mode=0o700)
    def gpg(*args): return subprocess.check_output(['gpg','--homedir',str(home),'--batch','--passphrase','',*args],stderr=subprocess.DEVNULL)
    gpg('--quick-generate-key','Distribution fixture <fixture@example.test>','ed25519','sign','1d')
    primary=gpg('--with-colons','--list-keys').decode().split('fpr:::::::::')[1].split(':')[0]
    public=root/'public.asc'; public.write_bytes(gpg('--armor','--export'))
    check('single reviewed public key staged',lambda:module.stage('https://packages.example.test/',public,primary,root/'staged'),True)
    check('wrong fingerprint rejected',lambda:module.stage('https://packages.example.test/',public,'0'*40,root/'bad'))
    secret=root/'secret.asc'; secret.write_bytes(gpg('--armor','--export-secret-keys'))
    check('private key material rejected',lambda:module.stage('https://packages.example.test/',secret,primary,root/'bad'))
    gpg('--quick-generate-key','Other fixture <other@example.test>','ed25519','sign','1d')
    public.write_bytes(gpg('--armor','--export'))
    check('extra primary key bundle rejected',lambda:module.stage('https://packages.example.test/',public,primary,root/'bad'))
print(f'{count} checks passed; only private temporary key fixtures were created.')
