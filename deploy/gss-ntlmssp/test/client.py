#!/usr/bin/env python3
"""Сырой клиент NTLMv2 для проверки акцептора gss-ntlmssp: флаги Type1 и порча MIC задаются снаружи.
Использование: client.py <флаги Type1 hex> <пароль> [--tamper-mic] [--f3=<флаги Type3 hex>] [--cbt] [--target=<SPN>]
  --f3     флаги в Type3 (по умолчанию пересечение Type1 и Type2); Windows шлёт 0xE2888235
  --cbt    положить в AV-пары ненулевой MsvAvChannelBindings (как Windows поверх TLS); --cbt=bad — заведомо чужой
  --target положить MsvAvTargetName (SPN, например HTTP/host)
  --kx-always  случайный ключ сессии при одном KEY_EXCH, без условия SIGN/SEAL (проверка обратной гипотезы)
  --win    Type1 и Type3 с настоящей структурой Version, как у Windows SSPI
  --http=URL  вместо ./acceptor идти на HTTPS-стенд Kestrel+Negotiate (заголовок Authorization)
  --h2        в --http идти по HTTP/2 (httpx[http2])
  --spnego    в --http заворачивать токены в SPNEGO (NegTokenInit/NegTokenResp), иначе сырой NTLMSSP
  --no-mechmic  в SPNEGO не класть mechListMIC (по умолчанию кладётся, как Windows)
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

def der(tag, body):
    n = len(body)
    ln = bytes([n]) if n < 128 else (b'\x81' + bytes([n]) if n < 256 else b'\x82' + struct.pack('>H', n))
    return bytes([tag]) + ln + body

OID_NTLM = bytes.fromhex('060a2b06010401823702020a')
OID_SPNEGO = bytes.fromhex('06062b0601050502')
MECH_TYPES = der(0x30, OID_NTLM)

def ntlm_mic_token(exported_key: bytes, data: bytes, flags: int) -> bytes:
    """Подпись NTLMSSP расширенной защиты (MS-NLMP 3.4.4.2) для mechListMIC: клиент→сервер, seq=0."""
    sign_key = hashlib.md5(exported_key + b'session key to client-to-server signing key magic constant\0').digest()
    seal_key = hashlib.md5(exported_key + b'session key to client-to-server sealing key magic constant\0').digest()
    chk = hmac_md5(sign_key, struct.pack('<I', 0) + data)[:8]
    if flags & KEY_EXCH: chk = rc4(seal_key, chk)
    return struct.pack('<I', 1) + chk + struct.pack('<I', 0)

def field(length, off): return struct.pack('<HHI', length, length, off)

def main():
    flags1 = int(sys.argv[1], 16); password = sys.argv[2]; tamper = '--tamper-mic' in sys.argv
    opt = {a.split('=')[0]: (a.split('=', 1)[1] if '=' in a else True) for a in sys.argv[3:]}
    win_version = b'\x0a\0\x63\x45\0\0\0\x0f' if '--win' in opt else b'\0' * 8
    user, domain = 'andrey', 'WORKGROUP'
    wrap = None  # в SPNEGO заворачиваем только на --http --spnego
    if '--http' in opt:
        import base64, http.client, ssl
        u = opt['--http'].split('://', 1)[1].split(':')
        ctx = ssl.create_default_context(); ctx.check_hostname = False; ctx.verify_mode = ssl.CERT_NONE
        conn = http.client.HTTPSConnection(u[0], int(u[1]), context=ctx); conn.connect()
        cert_der = conn.sock.getpeercert(binary_form=True)
        conn.request('GET', '/'); conn.getresponse().read()
        wrap = '--spnego' in opt; sent = {'n': 0}
        h2 = None
        if '--h2' in opt:  # HTTP/2 (нужен httpx[http2]): те же запросы одним соединением
            import httpx
            h2 = httpx.Client(http2=True, verify=False, base_url=opt['--http'])
            h2.get('/')
        def talk(tok: bytes):
            sent['n'] += 1
            if wrap:
                tok = (der(0x60, OID_SPNEGO + der(0xa0, der(0x30, der(0xa0, MECH_TYPES) + der(0xa2, der(0x04, tok)))))
                       if sent['n'] == 1 else tok)
            hdr = {'Authorization': 'Negotiate ' + base64.b64encode(tok).decode()}
            if h2 is not None:
                r = h2.get('/', headers=hdr)
                assert r.http_version == 'HTTP/2', r.http_version
                status, body, ch = r.status_code, r.content, r.headers.get('www-authenticate', '')
            else:
                conn.request('GET', '/', headers=hdr)
                r = conn.getresponse(); body = r.read(); status = r.status
                ch = r.getheader('WWW-Authenticate', '')
            if status == 200: return 'OK ' + body.decode(errors='replace')
            if sent['n'] == 1 and ' ' in ch:
                raw = base64.b64decode(ch.split(' ', 1)[1]); i = raw.find(b'NTLMSSP\0')
                return 'TOKEN ' + raw[i:].hex()
            return f'FAIL {status}'
    else:
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
    flags3 = int(opt['--f3'], 16) if '--f3' in opt else flags1 & flags2
    if '--verbose' in opt: print(f'flags1 {flags1:08X} flags2 {flags2:08X} flags3 {flags3:08X}', file=sys.stderr)

    ti = tinfo[:-4] + struct.pack('<HHI', 6, 4, 2)  # MsvAvFlags=0x2 (MIC)
    if '--singlehost' in opt:  # MsvAvSingleHost: Size=48, Z4, CustomData(8), MachineID(32), как у Windows
        ti += struct.pack('<HH', 8, 48) + struct.pack('<II', 48, 0) + b'\0' * 8 + os.urandom(32)
    if '--cbt' in opt:
        cb_app = b'tls-server-end-point:' + (hashlib.sha256(cert_der).digest() if '--http' in opt and opt['--cbt'] != 'bad' else b'test')
        # MS-NLMP 2.2.2.1: MD5 от структуры gss_channel_bindings (4 нулевых поля + длина + данные)
        ti += struct.pack('<HH', 10, 16) + hashlib.md5(struct.pack('<IIIII', 0, 0, 0, 0, len(cb_app)) + cb_app).digest()
    if '--target' in opt:
        tn = opt['--target'].encode('utf-16le'); ti += struct.pack('<HH', 9, len(tn)) + tn
    ti += b'\0\0\0\0'  # EOL
    nthash = md4(password.encode('utf-16le'))
    rk = hmac_md5(nthash, (user.upper() + domain).encode('utf-16le'))
    cchal = os.urandom(8)
    blob = b'\x01\x01\0\0\0\0\0\0' + struct.pack('<Q', int((time.time() + 11644473600) * 1e7)) + cchal + b'\0' * 4 + ti + b'\0' * 4
    proof = hmac_md5(rk, schal + blob); nt = proof + blob
    lm = b'\0' * 24 if '--lmzero' in opt else hmac_md5(rk, schal + cchal) + cchal
    kxk = hmac_md5(rk, proof)  # SessionBaseKey = KeyExchangeKey при NTLMv2

    enc = b''
    mic_key = kxk
    if flags3 & KEY_EXCH:
        exported = os.urandom(16); enc = rc4(kxk, exported)
        if flags3 & (SIGN | SEAL) or '--kx-always' in opt: mic_key = exported  # MS-NLMP 3.1.5.1.2; --kx-always — клиент без этого условия

    dom, usr, wks = (s.encode('utf-16le') for s in (domain, user, 'CLIENT'))
    off = 88
    parts = []
    def put(b):
        nonlocal off
        f = field(len(b), off); parts.append(b); off += len(b); return f
    f_lm, f_nt, f_dom, f_usr, f_wks, f_enc = put(lm), put(nt), put(dom), put(usr), put(wks), put(enc)
    hdr = b'NTLMSSP\0' + struct.pack('<I', 3) + f_lm + f_nt + f_dom + f_usr + f_wks + f_enc + struct.pack('<I', flags3) + win_version
    t3 = hdr + b'\0' * 16 + b''.join(parts)
    mic = hmac_md5(mic_key, t1 + t2 + t3)
    if tamper: mic = bytes([mic[0] ^ 1]) + mic[1:]
    t3 = t3[:72] + mic + t3[88:]
    if wrap:
        resp = der(0xa2, der(0x04, t3))
        if '--no-mechmic' not in opt and flags3 & (SIGN | SEAL):
            resp += der(0xa3, der(0x04, ntlm_mic_token(mic_key, MECH_TYPES, flags3)))
        t3 = der(0xa1, der(0x30, resp))
    print(talk(t3).replace('\n', ' '))
    return 0

sys.exit(main())
