"""Independent synthetic AES-CBC/CTR vectors; no KNX implementation imported."""
import json
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

def protect(kind, service, payload, sequence, tpci, serial=b"", header=None, response=False):
    key=bytes(range(16)); sa,da=(0x1101,0x11F0) if response else (0x11F0,0x1101)
    address=sa.to_bytes(2,"big")+da.to_bytes(2,"big"); q=sequence.to_bytes(6,"big")
    scf=bytes([(0x90 if kind=="tool" else 0x10)|service]); aad=scf+serial
    b0=q+address+bytes([0,0,tpci|3,0xF1,0,len(payload)])
    text=b0+len(aad).to_bytes(2,"big")+aad+payload; text+=bytes(-len(text)%16)
    enc=Cipher(algorithms.AES(key),modes.CBC(bytes(16))).encryptor()
    mac=(enc.update(text)+enc.finalize())[-16:-12]
    enc=Cipher(algorithms.AES(key),modes.CTR(q+address+bytes.fromhex("000000000100"))).encryptor()
    cipher=enc.update(mac+payload)+enc.finalize()
    return (bytes([tpci|3,0xF1])+scf+(sequence if header is None else header).to_bytes(6,"big")+serial+cipher[4:]+cipher[:4]).hex()

result={}
for kind in ["tool","p2p"]:
    for tpci in [0,0x6C]:
        result[f"{kind}_data_{tpci:02x}"]=protect(kind,0,bytes.fromhex("03D500071001"),0x010203040506,tpci)
        result[f"{kind}_sync_req_{tpci:02x}"]=protect(kind,2,bytes.fromhex("102030405060"),0x010203040506,tpci,bytes(6))
        result[f"{kind}_sync_res_{tpci:02x}"]=protect(kind,3,bytes.fromhex("010203040507010203040609"),0xAABBCCDDEEFF,tpci,header=0xAABBCCDDEEFF^0x102030405060,response=True)
print(json.dumps(result,indent=2))
