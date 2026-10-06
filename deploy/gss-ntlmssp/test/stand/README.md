# Дев-стенд NTLM (Kestrel + HTTPS + ASP.NET Negotiate)

Реальные `NegotiateFailure`, `NtlmHandshakeRecorder`, `NtlmMicProbe`, `NtlmUserFile` из бэкенда — за Kestrel
на `https://localhost:5443`. Нужен только для воспроизведения отказов NTLM; на боевой сервер не влияет.

```bash
cd deploy/gss-ntlmssp/test/stand
openssl req -x509 -newkey rsa:2048 -nodes -keyout k.pem -out c.pem -days 2 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost"
openssl pkcs12 -export -out c.pfx -inkey k.pem -in c.pem -passout pass:x
HASH=$(python3 -c "import sys; sys.argv=['x']; exec(open('../client.py').read().split('def rc4')[0]); print(md4('secret'.encode('utf-16le')).hex().upper())")
echo "WORKGROUP\\andrey:0:XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX:$HASH:[U          ]:LCT-00000000:" > users.txt
dotnet build -c Release -o out && cp c.pfx users.txt out/ && cd out
NTLM_USER_FILE=$PWD/users.txt GSSNTLMSSP_DEBUG=/tmp/gss.log dotnet stand.dll &
# клиент (Windows-профиль Type1/Type3, CBT, TargetName; сырой NTLM и SPNEGO):
python3 ../../client.py E2088237 secret --f3=E2888235 --win --cbt --target=HTTP/localhost --http=https://localhost:5443 --spnego
```

Тестовый пароль `secret` и пользователь `andrey` — только для стенда. Остановка — `kill <PID>` по номеру процесса,
не по имени.
