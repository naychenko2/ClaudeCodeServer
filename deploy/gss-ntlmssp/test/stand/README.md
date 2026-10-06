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

## Живой Windows против стенда

Нужен, когда спецификационный клиент `client.py` проходит, а настоящий Windows (Word, WebDAV-стек) — нет: гоним Windows на
тестовую учётку, а стенд печатает полные Type1/Type2/Type3 (`STAND_DUMP=1`; пароль известен, секретов нет) и пробу MIC.

```bash
# на Linux-машине: сертификат с SAN под имя/адрес, по которому Windows достаёт машину (здесь IP — подставить свой)
openssl req -x509 -newkey rsa:2048 -nodes -keyout k.pem -out c.pem -days 2 -subj "/CN=ccs-stand" \
  -addext "subjectAltName=DNS:ccs-stand,IP:<IP-машины>"
openssl pkcs12 -export -out c.pfx -inkey k.pem -in c.pem -passout pass:x
dotnet build -c Release -o out && cp c.pfx users.txt out/ && cd out
NTLM_USER_FILE=$PWD/users.txt STAND_LISTEN=any STAND_DUMP=1 dotnet stand.dll 2> /tmp/stand.log &   # порт 5443, ловить: grep DUMP /tmp/stand.log
```

На Windows (PowerShell), по возрастанию близости к Word; доменная часть логина обязательна — `WORKGROUP\andrey`, пароль `secret`:

```powershell
# 1) SSPI-NTLM напрямую (без проверки сертификата): Type1/Type3 штатного Windows SSPI
curl.exe -vk --ntlm -u "WORKGROUP\andrey:secret" https://<IP-машины>:5443/
# 2) Negotiate-обёртка Windows SSPI
curl.exe -vk --negotiate -u "WORKGROUP\andrey:secret" https://<IP-машины>:5443/
# 3) WinHTTP — тот же стек, что под WebDAV Office (сертификат стенда — в «Доверенные корневые», c.pem)
$r = New-Object -ComObject WinHttp.WinHttpRequest.5.1
$r.Open("OPTIONS", "https://<IP-машины>:5443/"); $r.SetCredentials("WORKGROUP\andrey", "secret", 0); $r.Send(); $r.Status
```

Результат: ищи в `/tmp/stand.log` строки `NTLM отклонён … MIC …` (какая гипотеза совпала и какие пары AV не вернулись из Type2) и `DUMP …`
(полные сообщения — по ним пробу можно гонять офлайн, не гоняя Windows снова). Порт 5443 должен быть открыт во входящих брандмауэра машины.
Остановка — `kill <PID>`, не по имени.
