import json
from cryptography.hazmat.primitives.ciphers import Cipher,algorithms,modes
def protect(key,source,group,seq,apdu,auth_only=False):
    scf=bytes([0 if auth_only else 16]);addr=source.to_bytes(2,'big')+group.to_bytes(2,'big');q=seq.to_bytes(6,'big')
    b0=q+addr+bytes.fromhex('008003f100')+bytes([0 if auth_only else len(apdu)])
    aad=scf+apdu if auth_only else scf;p=b'' if auth_only else apdu
    blocks=b0+len(aad).to_bytes(2,'big')+aad+p;blocks+=bytes((-len(blocks))%16)
    e=Cipher(algorithms.AES(key),modes.CBC(bytes(16))).encryptor();tag=(e.update(blocks)+e.finalize())[-16:-12]
    if not auth_only:
        e=Cipher(algorithms.AES(key),modes.CTR(q+addr+bytes.fromhex('000000000100'))).encryptor();out=e.update(tag+apdu)+e.finalize();tag,apdu=out[:4],out[4:]
    secure=bytes.fromhex('03f1')+scf+q+apdu+tag;ctrl=0xbc if len(secure)-1<=15 else 0x3c
    return (bytes([0x11,0,ctrl,0xe0])+addr+bytes([len(secure)-1])+secure).hex()
k=bytes(range(16));vectors={}
for only in [False,True]:
    for size in [2,3,4,15,16,17,32,242]:
        apdu=bytes([0,128])+bytes(range(size-2));vectors[f'{"auth" if only else "conf"}_{size}']=protect(k,0x1101,0x0101,0x010203040506,apdu,only)
print(json.dumps(vectors,indent=2))
