#!/usr/bin/env python3
"""Сырой клиент NTLMv2 для проверки акцептора gss-ntlmssp: флаги Type1 и порча MIC задаются снаружи.
Использование: client.py <флаги hex> <пароль> [--tamper-mic]
Запускает ./acceptor, прогоняет Type1 → Type2 → Type3, печатает итог одной строкой (OK/FAIL)."""
import hashlib, hmac, os, struct, subprocess, sys, time

def md4(data: bytes) -> bytes:
    def lrot(x, n): x &= 0xffffffff; return ((x << n) | (x >> (32 - n))) & 0xffffffff
    msg = data + b'\x80'
    msg += b'\0' * ((56 - len(msg) % 64) % 64) + struct.pack('<Q', len(data) * 8)
    a, b, c, d = 0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476
    for off in range(0, len(msg), 64):
        x = struct.unpack('<16I', msg[off:off + 64]); aa, bb, cc, dd = a, b, c, d
        F = lambda x, y, z: (x & y) | (~x & z)
        G = lambda x, y, z: (x & y) | (x & z) | (y & z)
        H = lambda x, y, z: x ^ y ^ z
        for i in range(16):
            s = (3, 7, 11, 19)[i % 4]
            a, b, c, d = d, lrot(a + F(b, c, d) + x[i], s), b, c
        for i in range(16):
            k = (i % 4) * 4 + i // 4; s = (3, 5, 9, 13)[i % 4]
            a, b, c, d = d, lrot(a + G(b, c, d) + x[k] + 0x5a827999, s), b, c
        for i in range(16):
            k = (0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15)[i]; s = (3, 9, 11, 15)[i % 4]
            a, b, c, d = d, lrot(a + H(b, c, d) + x[k] + 0x6ed9eba1, s), b, c
        a, b, c, d = (a + aa) & 0xffffffff, (b + bb) & 0xffffffff, (c + cc) & 0xffffffff, (d + dd) & 0xffffffff
    return struct.pack('<4I', a, b, c, d)

def rc4(key: bytes, data: bytes) -> bytes:
    s = list(range(256)); j = 0
    for i in range(256):
        j = (j + s[i] + key[i % len(key)]) & 255; s[i], s[j] = s[j], s[i]
    i = j = 0; out = bytearray()
    for byte in data:
        i = (i + 1) & 255; j = (j + s[i]) & 255; s[i], s[j] = s[j], s[i]
        out.append(byte ^ s[(s[i] + s[j]) & 255])
    return bytes(out)

hmac_md5 = lambda k, m: hmac.new(k, m, hashlib.md5).digest()
SIGN, SEAL, KEY_EXCH = 0x10, 0x20, 0x40000000

def field(length, off): return struct.pack('<HHI', length, length, off)

def main():
    flags1 = int(sys.argv[1], 16); password = sys.argv[2]; tamper = '--tamper-mic' in sys.argv
    user, domain = 'andrey', 'WORKGROUP'
    acc = subprocess.Popen([os.path.join(os.path.dirname(os.path.abspath(__file__)), 'acceptor')],
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
    def talk(tok: bytes):
        acc.stdin.write(tok.hex() + '\n'); acc.stdin.flush()
        return acc.stdout.readline().strip()

    t1 = b'NTLMSSP\0' + struct.pack('<II', 1, flags1) + field(0, 40) + field(0, 40) + b'\x0a\0\x63\x45\0\0\0\x0f'
    r = talk(t1)
    if not r.startswith('TOKEN '): print('FAIL на Type1:', r); return 1
    t2 = bytes.fromhex(r[6:])
    flags2 = struct.unpack_from('<I', t2, 20)[0]; schal = t2[24:32]
    tl, _, to = struct.unpack_from('<HHI', t2, 40); tinfo = t2[to:to + tl]
    flags3 = flags1 & flags2

    ti = tinfo[:-4] + struct.pack('<HHI', 6, 4, 2) + b'\0\0\0\0'  # MsvAvFlags=0x2 (MIC), затем EOL
    nthash = md4(password.encode('utf-16le'))
    rk = hmac_md5(nthash, (user.upper() + domain).encode('utf-16le'))
    cchal = os.urandom(8)
    blob = b'\x01\x01\0\0\0\0\0\0' + struct.pack('<Q', int((time.time() + 11644473600) * 1e7)) + cchal + b'\0' * 4 + ti + b'\0' * 4
    proof = hmac_md5(rk, schal + blob); nt = proof + blob
    lm = hmac_md5(rk, schal + cchal) + cchal
    kxk = hmac_md5(rk, proof)  # SessionBaseKey = KeyExchangeKey при NTLMv2

    enc = b''
    mic_key = kxk
    if flags3 & KEY_EXCH:
        exported = os.urandom(16); enc = rc4(kxk, exported)
        if flags3 & (SIGN | SEAL): mic_key = exported  # MS-NLMP 3.1.5.1.2

    dom, usr, wks = (s.encode('utf-16le') for s in (domain, user, 'CLIENT'))
    off = 88
    parts = []
    def put(b):
        nonlocal off
        f = field(len(b), off); parts.append(b); off += len(b); return f
    f_lm, f_nt, f_dom, f_usr, f_wks, f_enc = put(lm), put(nt), put(dom), put(usr), put(wks), put(enc)
    hdr = b'NTLMSSP\0' + struct.pack('<I', 3) + f_lm + f_nt + f_dom + f_usr + f_wks + f_enc + struct.pack('<I', flags3) + b'\0' * 8
    t3 = hdr + b'\0' * 16 + b''.join(parts)
    mic = hmac_md5(mic_key, t1 + t2 + t3)
    if tamper: mic = bytes([mic[0] ^ 1]) + mic[1:]
    t3 = t3[:72] + mic + t3[88:]
    print(talk(t3).replace('\n', ' '))
    return 0

sys.exit(main())
