import hashlib,json
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
from cryptography.hazmat.primitives.ciphers import Cipher,algorithms,modes
def frame(s,b): return bytes.fromhex('0610')+s.to_bytes(2,'big')+(len(b)+6).to_bytes(2,'big')+b
def tag(k,a,p=b'',b=bytes(16)):
    msg=b+len(a).to_bytes(2,'big')+a+p
    msg+=bytes((-len(msg))%16)
    e=Cipher(algorithms.AES(k),modes.CBC(bytes(16))).encryptor()
    return (e.update(msg)+e.finalize())[-16:]
def ctr(k,c,m,p=b''):
    e=Cipher(algorithms.AES(k),modes.CTR(c)).encryptor(); r=e.update(m+p)+e.finalize();return r[:16],r[16:]
def wrap(k,s,q,z,p):
    h=bytes.fromhex('06100950')+(len(p)+38).to_bytes(2,'big')
    si=s.to_bytes(2,'big');info=q.to_bytes(6,'big')+z+bytes(2)
    m=tag(k,h+si,p,info+len(p).to_bytes(2,'big'))
    em,ep=ctr(k,info+bytes.fromhex('ff00'),m,p)
    return h+si+info+ep+em
alice=X25519PrivateKey.from_private_bytes(bytes.fromhex('77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a'))
bob=X25519PrivateKey.from_private_bytes(bytes.fromhex('5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb'))
ap=alice.public_key().public_bytes_raw();bp=bob.public_key().public_bytes_raw();xor=bytes(a^b for a,b in zip(ap,bp))
k=bytes(range(16));user=bytes(range(16,32));dev=bytes(range(32,48));serial=bytes.fromhex('112233445566');sid=0x1234
counter=bytes(14)+bytes.fromhex('ff00')
response=frame(0x952,sid.to_bytes(2,'big')+bp+bytes(16));mac=tag(dev,response[:8]+xor);response=response[:-16]+ctr(dev,counter,mac)[0]
auth=frame(0x953,bytes.fromhex('0002')+bytes(16));mac=tag(user,auth[:8]+xor);auth=auth[:-16]+ctr(user,counter,mac)[0]
session_key=hashlib.sha256(alice.exchange(bob.public_key())).digest()[:16]
vectors={'wrapper':wrap(k,sid,0x010203040506,serial,bytes.fromhex('0610020800087b00')).hex(),
 'response':response.hex(),'auth_plain':auth.hex(),'auth_wrapped':wrap(session_key,sid,0,serial,auth).hex(),
 'status_wrapped':wrap(session_key,sid,0,bytes.fromhex('aabbccddeeff'),frame(0x954,bytes(2))).hex(),
 'user_kdf':hashlib.pbkdf2_hmac('sha256',b'Synthetic secure password',b'user-password.1.secure.ip.knx.org',65536,16).hex(),
 'device_kdf':hashlib.pbkdf2_hmac('sha256',b'Synthetic authentication',b'device-authentication-code.1.secure.ip.knx.org',65536,16).hex()}
print(json.dumps(vectors,indent=2))
