#!/usr/bin/env bash
# Проверка акцептора gss-ntlmssp на флагах Type1 и порче MIC. Запускать внутри контейнера
# (build-deb.sh --test делает это сам). Аргументы: путь к .so; печатает таблицу результатов.
# Эталонный пользователь: WORKGROUP\andrey, пароль 'secret'.
set -u
SO="$1"
cd "$(dirname "$0")"
gcc -o acceptor acceptor.c -lgssapi_krb5 || exit 1
T=$(mktemp -d)
echo "gssntlmssp_v1 1.3.6.1.4.1.311.2.2.10 $SO" > "$T/mech.conf"
# NT-хэш 'secret' берём из нашего же клиента (MD4 на чистом python)
HASH=$(python3 -c "import sys; sys.argv=['x']; exec(open('client.py').read().split('def rc4')[0]); print(md4('secret'.encode('utf-16le')).hex().upper())")
echo "WORKGROUP\\andrey:0:XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX:$HASH:[U          ]:LCT-00000000:" > "$T/users"
export GSS_MECH_CONFIG="$T/mech.conf" NTLM_USER_FILE="$T/users"
run() { echo "$1 | $(python3 client.py "${@:2}" 2>&1 | tail -1)"; }
run "0xE2088207 (KEY_EXCH без SIGN/SEAL)      " E2088207 secret
run "0xE2088237 (SIGN+SEAL)                   " E2088237 secret
run "0xE2088217 (SIGN)                        " E2088217 secret
run "0xE2088227 (SEAL)                        " E2088227 secret
run "0xE2088207, неверный пароль              " E2088207 wrong
run "0xE2088237, неверный пароль              " E2088237 wrong
run "0xE2088207, подменённый MIC              " E2088207 secret --tamper-mic
run "0xE2088237, подменённый MIC              " E2088237 secret --tamper-mic
rm -rf "$T" acceptor
