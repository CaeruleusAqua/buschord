#!/usr/bin/env python3
"""Validate a single reviewed public repository key and stage its package-owned APT configuration."""
import ipaddress, pathlib, subprocess, sys, tempfile, urllib.parse

def require(condition):
    if not condition: raise ValueError('Unsupported repository configuration')

def validate_url(value):
    require(all(32 < ord(c) < 127 for c in value))
    uri=urllib.parse.urlsplit(value)
    require(uri.scheme == 'https' and uri.hostname and '.' in uri.hostname)
    require(uri.port in (None,443) and not uri.username and not uri.password and not uri.query and not uri.fragment)
    try: ipaddress.ip_address(uri.hostname)
    except ValueError: pass
    else: raise ValueError('IP literals are unsupported')

def stage(url, key, expected, destination):
    validate_url(url)
    data=pathlib.Path(key).read_text(encoding='ascii')
    require(len(data) < 1024*1024 and data.startswith('-----BEGIN PGP PUBLIC KEY BLOCK-----'))
    require('PRIVATE KEY BLOCK' not in data)
    with tempfile.TemporaryDirectory(prefix='buschord-public-key-validation-') as isolated:
        pathlib.Path(isolated).chmod(0o700)
        metadata=subprocess.check_output(['gpg','--no-options','--homedir',isolated,'--batch','--show-keys','--with-colons',str(key)],text=True,stderr=subprocess.DEVNULL)
    lines=[line.split(':') for line in metadata.splitlines()]
    require(sum(row[0]=='pub' for row in lines)==1)
    primary=next(row[9] for row in lines if row[0]=='fpr')
    require(primary == expected.replace(' ','').upper())
    root=pathlib.Path(destination)
    source=root/'etc/buschord-updater'; source.mkdir(parents=True,exist_ok=True)
    keyring=root/'usr/share/keyrings'; keyring.mkdir(parents=True,exist_ok=True)
    (keyring/'buschord-archive.asc').write_text(data,encoding='ascii')
    (source/'buschord.sources').write_text('Types: deb\nURIs: '+url+'\nSuites: stable\nComponents: main\nArchitectures: amd64\nSigned-By: /usr/share/keyrings/buschord-archive.asc\nCheck-Valid-Until: yes\n')
    (source/'enabled').touch()
    for path in (keyring/'buschord-archive.asc',source/'buschord.sources',source/'enabled'): path.chmod(0o644)

if __name__=='__main__':
    try:
        require(len(sys.argv)==5); stage(*sys.argv[1:]); print('Validated public APT source staged.')
    except Exception as error:
        print('APT source setup refused: '+type(error).__name__,file=sys.stderr); sys.exit(1)
