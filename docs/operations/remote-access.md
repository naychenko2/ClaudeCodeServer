# Удалённый доступ к Claude Home

Сервер должен быть доступен **дома по WiFi** и **снаружи через мобильную сеть**.
Решение состоит из двух частей: транспорт (как дотянуться до сервера) и аутентификация
(как не пустить чужих).

```
   дом / WiFi                          мобильная сеть / интернет
       │                                        │
       ▼                                        ▼
  http://<IP>:5000              https://<домен>.duckdns.org  (проброс порта + DDNS)
  (уже работает)                            │
       │                              роутер: 443/80 → ПК
       │                                    ▼
       │                         Caddy :443 (Let's Encrypt TLS)
       │                                    │ reverse_proxy
       └────────────────┬───────────────────┘
                        ▼
            ASP.NET Core :5000 (0.0.0.0)
            [Authorize] на всех API + Hub  ·  rate-limit на /api/auth/login
            доступ только по API-ключу
```

## 1. Локальный доступ (дома, по WiFi)

Уже работает из коробки — сервер слушает `0.0.0.0:5000`:

1. Узнай IP домашнего ПК: `ipconfig` → строка `IPv4-адрес` (например `192.168.1.50`).
2. С телефона в той же WiFi-сети открой `http://192.168.1.50:5000`.
3. Если не открывается — разреши порт в брандмауэре Windows:
   ```powershell
   New-NetFirewallRule -DisplayName "ClaudeHomeServer" -Direction Inbound `
       -Protocol TCP -LocalPort 5000 -Action Allow
   ```

> По HTTP PWA не устанавливается как приложение (нужен HTTPS либо localhost), но в
> браузере всё работает. Для установки PWA используй HTTPS-адрес из п. 2 ниже.

## 2. Внешний доступ (мобильная сеть) — проброс порта + DDNS

### Шаг 0. Проверь, есть ли БЕЛЫЙ IP (критично!)

Проброс работает только если домашний провайдер выдаёт **публичный (белый) IP**.
При CGNAT (часто у мобильных и части домашних тарифов) проброс **не сработает** —
переходи к разделу «Если белого IP нет».

```powershell
# Внешний IP, как его видит интернет:
(Invoke-RestMethod https://api.ipify.org)
```
Сравни его с IP в админке роутера (раздел WAN/Состояние). Если совпадают — IP белый.
Если IP роутера вида `100.64.x.x`–`100.127.x.x` или отличается от ipify — это CGNAT.

### Шаг 1. Статичный локальный IP для ПК

Закрепи за ПК IP в роутере (DHCP reservation) или задай статический — чтобы проброс
не «съезжал» при перезагрузке.

### Шаг 2. DDNS — стабильное имя для меняющегося IP

Домашний внешний IP обычно динамический. DDNS привязывает имя к текущему IP.

1. Заведи поддомен на <https://www.duckdns.org> (вход через Google/GitHub) — получишь
   `<домен>.duckdns.org` и токен.
2. Настрой автообновление: либо в роутере (раздел **DDNS** → провайдер DuckDNS/No-IP),
   либо скриптом-задачей на ПК (DuckDNS даёт готовую команду обновления).

### Шаг 3. HTTPS через Caddy (reverse-proxy, авто Let's Encrypt)

PWA и Service Worker требуют HTTPS. Caddy сам получает и продлевает сертификат.

1. Скачай Caddy: <https://caddyserver.com/download> (один exe).
2. Рядом положи файл `Caddyfile`:
   ```
   <домен>.duckdns.org {
       reverse_proxy localhost:5000
   }
   ```
3. Запусти: `caddy run` (или установи службой: `caddy start`).
4. Брандмауэр Windows — разреши 80 и 443:
   ```powershell
   New-NetFirewallRule -DisplayName "Caddy HTTP"  -Direction Inbound -Protocol TCP -LocalPort 80  -Action Allow
   New-NetFirewallRule -DisplayName "Caddy HTTPS" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow
   ```

### Шаг 4. Проброс портов на роутере

В админке роутера (Port Forwarding / Виртуальный сервер) пробрось на локальный IP ПК:

| Внешний порт | → | ПК (внутр.) | Зачем |
|---|---|---|---|
| 80  | → | 80  | HTTP-01 challenge Let's Encrypt (выдача сертификата) |
| 443 | → | 443 | основной HTTPS-трафик |

> Caddy слушает 80/443 и проксирует на `localhost:5000`. Сам сервер ASP.NET наружу
> пробрасывать **не нужно** — только через Caddy.

### Шаг 5. Проверка

С телефона **по мобильной сети** (WiFi выключен) открой
`https://<домен>.duckdns.org` → страница входа, вводи API-ключ. PWA можно установить.

### Если белого IP нет (CGNAT)

Проброс невозможен — нужен исходящий туннель или VPN. Варианты:

- **Cloudflare Tunnel** — `cloudflared` на ПК даёт публичный HTTPS-домен, работает за
  CGNAT, на телефоне ничего не нужно. Сервер тоже становится публичным → защищён нашим
  ключом (плюс можно Cloudflare Access).
- **Tailscale** (VPN-mesh) — приватный доступ только со своих устройств, на телефоне
  активен VPN-профиль. `tailscale serve --bg 5000` даёт HTTPS. Самый безопасный, но
  требует клиент на телефоне.

## 3. Аутентификация (обязательна)

Все API-эндпоинты и SignalR-хаб закрыты атрибутом `[Authorize]`; схема — **JWT Bearer**.
Токен выдаёт `POST /api/auth/login` по паре `{ username, password }`, он же дополнительно
ограничен по частоте (защита от перебора пароля).

### Откуда берётся учётная запись

Пользователи живут в `data/users.json` (`UserStore`). На первом запуске, когда файла ещё
нет, заводится администратор `admin` со **случайным** паролем — он печатается в консоль
ОДИН раз, при этом создании:
```
║  Логин: admin                               ║
```
Предсказуемой пары `admin/admin` не бывает по конструкции: пароль генерируется и хранится
хешем. Потерян — заводить заново через правку стора, отдельной команды сброса нет.

### Rate-limit входа

`POST /api/auth/login` ограничен фиксированным окном (политика `auth-login`, по умолчанию
**10 попыток/мин**, партиция по адресу клиента). Превышение → `429 Too Many Requests`
с заголовком `Retry-After`. Лимит настраивается:
```powershell
$env:Auth__LoginRateLimit = "20"
```
За reverse-proxy (Caddy) реальный IP клиента берётся из `X-Forwarded-For` — лимит
считается по клиенту, а не по адресу прокси.

### Как клиент передаёт ключ

- Страница входа сохраняет токен под ключом `cc_token`: в `localStorage` при галке
  «запомнить меня», иначе в `sessionStorage` (выбор хранилища — на входе, не в вызывающем коде).
- REST-запросы уходят с заголовком `Authorization: Bearer <токен>`.
- WebSocket (SignalR) передаёт токен как `?access_token=<токен>` (заголовок там задать нельзя).
- На ответ `401` фронт автоматически разлогинивает и возвращает на экран входа.

### Смена пароля

`PUT /api/auth/password` из интерфейса. Выданные до смены токены продолжают действовать
до своего истечения — отзыва по факту смены пароля нет.

## 4. WebDAV (сетевой диск Windows)

Проекты отдаются по WebDAV на `/projects/{проект}/…` (Explorer, Word/Excel, LOCK/UNLOCK).
Вход — **тем же логином и основным паролем**, что и в веб. Схем две:

- **Basic** — всегда. Explorer шлёт пароль при каждом подключении и часто его не запоминает.
- **NTLM (Negotiate)** — предлагается, только если Type3 есть чем проверить и запрос пришёл
  **по HTTPS**. С NTLM Windows берёт учётку из диспетчера учётных данных, и «Запомнить»
  начинает работать после перезагрузки. На Windows-хосте Type3 проверяет SSPI, на Linux —
  gss-ntlmssp по нашему файлу.

### Подключить диск

```powershell
net use P: \\<хост>@SSL\DavWWWRoot\projects\<проект> /user:<логин> /persistent:yes
```
Можно и через «Подключить сетевой диск» в Explorer с тем же адресом и галкой
«Использовать другие учётные данные». Если логин не проходит, введите его с доменом:
`WORKGROUP\<логин>` или `<ИМЯ-ХОСТА>\<логин>` (см. «Домены» ниже).

### Настройка NTLM на Linux (gss-ntlmssp)

1. Пакеты: `sudo apt install gss-ntlmssp libgssapi-krb5-2`. Механизм регистрирует файл
   `/etc/gss/mech.d/mech.ntlmssp.conf`, его кладёт сам пакет.
2. Каталог секретов вне `data/`: `sudo install -d -m 0700 -o <сервисный пользователь> /srv/ccs/secrets`.
3. В юните `deploy/systemd/ccs.service` задана `Environment=NTLM_USER_FILE=/srv/ccs/secrets/ntlm_users`.
   gss-ntlmssp читает путь из **среды процесса**, поэтому задавать его нужно только в юните.
   Тот же путь положите в `WebDav:NtlmUserFile` в `appsettings.Local.json` боевой машины: сервер
   пишет файл **только** по этому ключу. Переменную наследуют дочерние процессы, в том числе
   дев-стенды из ходов, и по одной переменной такой стенд писал бы в боевой файл.
4. `WebDav:NtlmMapWindowsNames: true` в `appsettings.Local.json`. Это opt-in сопоставления имени
   из Type3 с пользователем приложения. На Linux имя берётся из нашего же файла, и пароль к этому
   моменту уже проверен, но гейт один на обе платформы.
5. Файл пишет сервер сам: при входе в веб, смене или сбросе пароля, создании пользователя и
   успешном Basic-входе WebDAV. Пока пользователь после выкатки ни разу не вошёл, строки у него
   нет, и NTLM откатывается на Basic. Так и задумано. Удаление и переименование пользователя
   убирают его строки. Под новым именем строка появится при следующем входе.

Пока нет механизма, переменной, совпадения путей или самого файла, сервер предлагает только
Basic. Иначе Mini-Redirector цепляется за Negotiate и крутит обречённое рукопожатие по кругу.

### Домены

Домен из Type3 входит в NTLMv2-хэш **с учётом регистра**, а gss-ntlmssp берёт первую строку, где
домен совпал без учёта регистра. Поэтому на каждого пользователя пишется строка на каждый домен
из `WebDav:NtlmDomains` (по умолчанию `WORKGROUP` и имя хоста заглавными) и строка без домена
последней. Какой домен прислал клиент, видно в логе: неудачный Type3 пишет предупреждение
`NTLM отклонён: домен '…', пользователь '…'`, успешный — `WebDAV: NTLM-вход ДОМЕН\имя`.
Если домена клиента нет в списке, добавьте его в `WebDav:NtlmDomains` и войдите в веб, чтобы
файл переписался.

### Неверный пароль

На неудачном Type3 gss-ntlmssp бросает исключение. Событие `OnAuthenticationFailed`
(`WebDav/NegotiateFailure.cs`) превращает его в `401` с **одним** Basic. Без этого клиент получал
500, а Explorer крутил сохранённую учётку по кругу.

### Разбор InvalidToken и живая проверка с Windows

Статус в `NTLM отклонён: …: <статус>` различает причины (разбор 2026-10-05):
`GenericFailure` — не сошёлся NTLMv2-ответ (пароль, регистр или написание домена);
`InvalidToken` — gss-ntlmssp вернул `GSS_S_DEFECTIVE_TOKEN` **после** успешной проверки хэша, то
есть сломалась проверка MIC, channel bindings или разбор AV-пар. Неверный хэш InvalidToken не даёт.
На стенде против настоящего gss-ntlmssp 1.2.0 и Kestrel по HTTPS (NTLMv2, MIC, SPNEGO mechListMIC,
`MsvAvChannelBindings`, `MsvAvTargetName`) клиент pyspnego проходит; channel bindings Kestrel
на Linux не навязывает (даже заведомо неверный CBT принят).

Живая проверка (на Windows, не на проде без согласования):

1. На сервере включить трассировку: в юните `Environment=GSSNTLMSSP_DEBUG=/tmp/gssntlm.log`,
   перезапуск; после проверки убрать.
2. С Windows: `curl.exe -v --ntlm -u "WORKGROUP\andrey:ПАРОЛЬ" https://хост/projects/` и то же с
   `--negotiate -u "WORKGROUP\andrey:ПАРОЛЬ"`; для сравнения `-k` не нужен, если сертификат доверенный.
3. В логе приложения найти `NTLM отклонён …; Type3: флаги 0x…, NTLMv2, MsvAvFlags 0x2 (MIC),
   MsvAvTargetName …, CBT задан`. Прислать эту строку, статус и строки `ERROR:` из
   `/tmp/gssntlm.log` — по ним видно, на какой проверке (`gss_sec_ctx.c`: MIC, CBT) падает токен.

#### Причина InvalidToken найдена (2026-10-06): KEY_EXCH без SIGN/SEAL

`GSSNTLMSSP_DEBUG` на бою показал `gssntlm_accept_sec_context() @ gss_sec_ctx.c:976 [589824:13]`:
хэш прошёл, не сошёлся MIC. Воспроизведено на стенде (gss-ntlmssp 1.2.0 + Kestrel, клиент pyspnego):
ломает **не** сырой NTLM против SPNEGO (оба варианта проходят), а флаги Type1. Клиент, который просит
`KEY_EXCH`, но не просит `SIGN`/`SEAL` (Windows SSPI в HTTP-стиле), по MS-NLMP шлёт в качестве
ключа сессии сам KeyExchangeKey; gss-ntlmssp расшифровывает ключ по одному флагу `KEY_EXCH`
(`gss_sec_ctx.c`, ветка перед проверкой MIC; в `main` и 1.3.x тот же код) и получает чужой ключ.

| Type1 клиента | Результат |
|---|---|
| `0xE2088237` (SIGN+SEAL, pyspnego по умолчанию) | 200 |
| `0xE2088217` (SIGN) / `0xE2088227` (SEAL) | 200 |
| `0xE2088207` (без SIGN/SEAL) | 401, `InvalidToken`, `:976` |

Исправить на нашей стороне без своего NTLM-акцептора нельзя: MIC считается по Type1/Type2, а оба
хранит и сверяет сама gss-ntlmssp. Лог отказа теперь помечает такой Type3 фразой
«KEY_EXCH без SIGN/SEAL» — это и есть подтверждение на боевом клиенте.

Повтор на стенде (ключи — `NTLM_USER_FILE` со строкой `WORKGROUP\andrey` и NT-хэшем `secret`):
`spnego.client('WORKGROUP\\andrey', 'secret', protocol='ntlm', context_req=spnego.ContextReq.none)`,
шаги `step()` по HTTP-соединению keep-alive, токены в `Authorization: Negotiate`.

#### Исправление: пропатченный gss-ntlmssp (решение 2026-10-06)

Патч [ntlm-key-exch-requires-sign-seal.diff](../../deploy/gss-ntlmssp/ntlm-key-exch-requires-sign-seal.diff)
приводит gss-ntlmssp к MS-NLMP 3.1.5.1.2 / 3.2.5.1.2: ключ сессии расшифровывается только при
`KEY_EXCH` вместе с `SIGN` или `SEAL`, иначе ExportedSessionKey = KeyExchangeKey. **Проверка MIC не
ослаблена**: меняется только выбор ключа. Пакет — `1.2.0-1build5+ccs1` (Ubuntu 26.04), собирается
[build-deb.sh](../../deploy/gss-ntlmssp/build-deb.sh) в контейнере; готовый .deb после сборки лежит в
`deploy/gss-ntlmssp/out/` (в git не коммитится). Тесты матрицы флагов — `build-deb.sh --test`.

Результат на стенде (сырой NTLMv2-клиент против GSSAPI-акцептора, `test/run-test.sh`):

| Сценарий | оригинал | `+ccs1` |
|---|---|---|
| Type1 `0xE2088207` (KEY_EXCH без SIGN/SEAL) | отказ `[589824:13]` | вход |
| `0xE2088237` / `0xE2088217` / `0xE2088227` | вход | вход |
| неверный пароль (любые флаги) | отказ | отказ |
| подменённый MIC (`0xE2088207` и `0xE2088237`) | отказ | отказ |

Что проверено и что нет: акцептор — на матрице выше. Пакет правит только акцептор
(`gss_sec_ctx.c`); сторона инициатора (`gss_auth.c`) сознательно не тронута — непроверенный код в системной
библиотеке нам не нужен. Через Kestrel и Windows
.deb не гоняли — это живая проверка ниже.

**Установка (делает человек, на хосте с боевым `ccs.service`).** Пакет: `/home/an/ccs-packages/gss-ntlmssp_1.2.0-1build5+ccs1_amd64.deb`, sha256 `6ab41eb5c118eb567cdd916e437fcfbff37f1e47a9267e58c1e506789f738cac`.

```bash
sha256sum gss-ntlmssp_1.2.0-1build5+ccs1_amd64.deb   # сверить с суммой выше
sudo dpkg -i gss-ntlmssp_1.2.0-1build5+ccs1_amd64.deb
sudo apt-mark hold gss-ntlmssp
sudo systemctl restart ccs.service
deploy/gss-ntlmssp/check-gss-ntlmssp.sh        # exit 0: патч и hold на месте
```

**Живая проверка с Windows:** `curl.exe -v --ntlm -u "WORKGROUP\andrey:ПАРОЛЬ" https://хост/projects/` и то же с
`--negotiate`; затем открыть документ по WebDAV-адресу из Word. Ожидается `200`/открытие файла и в логе
`WebDAV: NTLM-вход WORKGROUP\andrey` без `NTLM отклонён … InvalidToken`.

**Сторож отката.** `apt upgrade` без hold молча вернёт оригинал. Защиты две: hold и проверка
[check-gss-ntlmssp.sh](../../deploy/gss-ntlmssp/check-gss-ntlmssp.sh) (exit 1 — версия без `+ccs`, exit 2 —
нет hold; печатает `WARNING` в stderr; добавьте вызов в `/opt/ccs/check-release.sh` или cron). Плюс
сервер при старте пишет warning `gss-ntlmssp … без патча '+ccs'` (`NtlmUserFile`), если читает
`/var/lib/dpkg/status` и версия без суффикса.

**Откат:** `sudo apt-mark unhold gss-ntlmssp && sudo apt install --reinstall gss-ntlmssp=1.2.0-1build5`
(при отсутствии версии в индексе — `sudo apt install --reinstall gss-ntlmssp`), затем
`sudo systemctl restart ccs.service`. Без патча NTLM у клиентов с `0xE2088207` снова даст InvalidToken, Basic
продолжит работать.

**Upstream.** Текст issue и PR — [upstream-issue-and-pr.md](../../deploy/gss-ntlmssp/upstream-issue-and-pr.md),
не опубликован: отправка только по явной просьбе.

#### Разбор 2026-10-06 (вечер): +ccs1 не помог, Type3 реального Windows — 0xE2888235

После установки `+ccs1` Windows всё равно получает отказ, и на этот раз в Type3 **есть** SIGN и SEAL
(`0xE2888235`: `SIGN|SEAL|KEY_EXCH|128|56`, NTLMv2, MIC, ненулевой CBT, `MsvAvTargetName HTTP/naychenko.me`).
Вывод «причина — KEY_EXCH без SIGN/SEAL» к этому клиенту **не относится**: патч для него ничего не меняет
(ключ расшифровывается и без патча). Причина **не найдена**: на стенде отказ не воспроизводится.

Что проверено на стенде (gss-ntlmssp 1.2.0 и `+ccs1`, Kestrel по HTTPS, NTLMv2 + MIC; клиент
[client.py](../../deploy/gss-ntlmssp/test/client.py) собирает Type1/Type3 по MS-NLMP, стенд —
[test/stand](../../deploy/gss-ntlmssp/test/stand)) — все варианты проходят и без патча, и с ним:

| Что варьировали | Результат |
|---|---|
| Type1 `0xE2088297/237/207/235`, Type3 `0xE2888235`, структура Version в Type1 и Type3 | OK |
| ненулевой `MsvAvChannelBindings`, `MsvAvTargetName`, `MsvAvSingleHost`, нулевой LM-ответ | OK |
| сырой NTLMSSP в `Negotiate` и SPNEGO (NegTokenInit/NegTokenResp, mechListMIC по MS-NLMP 3.4) | OK |
| прямой GSSAPI-акцептор без .NET (`run-test.sh`) | OK |

Опровергнутые гипотезы: **MIT SPNEGO подменяет Type1** — нет, MIC сходится и в сыром, и в обёрнутом виде;
**.NET искажает Type2** — нет, записанный на сервере Type2 совпадает с тем, по которому клиент считал
NTProofStr и MIC; **CBT ломает MIC** — нет, и более того, ASP.NET Negotiate на Linux **не передаёт channel
bindings** в GSSAPI вообще (заведомо чужой CBT принимается), так что CBT сервером не проверяется;
**RC4-расшифровка EncryptedRandomSessionKey** — корректна при `KEY_EXCH+SIGN|SEAL`. Попутно: по
**HTTP/2** хендлер Negotiate не аутентифицирует вовсе (на любой Authorization отвечает «challenged»), так что
до gss-ntlmssp Windows доходит только по HTTP/1.1.

Остаётся то, чего стенд знать не может: **реальные байты Type1/Type2/Type3 от Windows**. Чтобы не гонять
токены с паролем, сервер теперь диагностирует отказ сам: `NtlmHandshakeRecorder` держит в памяти Type1/Type2
соединения, а при отказе `NtlmMicProbe` пересчитывает MIC по NT-хэшу из `NTLM_USER_FILE` при каждой
гипотезе (ключ = KeyExchangeKey или RC4(KeyExchangeKey, EncryptedRandomSessionKey); каждый записанный
Type1; смещение MIC 72/64) и пишет в лог **только признаки**: какая гипотеза сошлась, флаги Type1/2/3,
размер EncryptedRandomSessionKey. Ключи, хэш и подписи в лог не попадают, токены не сохраняются.

Строка отказа теперь: `NTLM отклонён: домен…, пользователь…: <статус>; транспорт: <…>; Type3: <…>; MIC: <…>`.

- `транспорт`: `сырой NTLMSSP` (заголовок начинается с `TlRMTVNT`) или `SPNEGO NegTokenResp с/без mechListMIC`
  (ASN.1: `0x60` — NegTokenInit, `0xA1` — NegTokenResp).
- `MIC: MIC СХОДИТСЯ при: <ключ>; Type1 №N; смещение MIC …` — подпись клиента сходится при этой гипотезе, а
  gss-ntlmssp выбирает другую: это и есть причина (например, `KeyExchangeKey` при `SIGN|SEAL` значит, что
  клиент не шифровал ключ, а сервер расшифровывает; `RC4(…)` при отсутствии SIGN/SEAL — наоборот).
- `MIC не сходится ни в одной из N комбинаций (NTProofStr верен)` — расходятся сами сообщения (Type1/Type2/Type3),
  а не ключ: смотреть `Type1 записано N` и флаги Type1/Type2 в той же строке.
- `NTProofStr не сходится` — это не MIC, а пароль/хэш/написание домена.

**Живая проверка с Windows (одна попытка, без Wireshark и без записи токенов).**
1. Тестовый пользователь с тестовым паролем, не основной: Type3 содержит NTLMv2-ответ, по нему пароль подбирается офлайн.
2. Подключить `\\naychenko.me@SSL\DavWWWRoot\projects` (Проводник → «Подключить сетевой диск») или
   `net use * https://naychenko.me/projects /user:andrey`. HTTP/1.1 — это обязательно (см. про HTTP/2 выше).
3. В `/srv/ccs/data/logs/server-<дата>.log` найти `NTLM отклонён` и приложить строку целиком — в ней уже всё
   нужное для вывода. В боевом логе приложения 6 октября строк `NTLM` не оказалось — проверить уровень логов
   `ClaudeHomeServer.WebDav` (нужен Warning), иначе диагностика не видна.
4. Без боевого сервера: дев-стенд [test/stand](../../deploy/gss-ntlmssp/test/stand) на другом порту и
   Windows `curl.exe --negotiate -u andrey:secret -k https://<хост-стенда>:5443/` (предположительно curl на Windows
   ходит через SSPI и даёт настоящие Type1/Type3; не проверено — доступа к Windows не было; стенд пишет ту же
   диагностическую строку). Виртуалка `windows` из
   `docs/operations/linux-dualboot-kvm-windows-passthrough.md` для этого подходит; её запускает человек.

### Безопасность

В файле лежат NT-хэши **основного пароля**: по ним возможен pass-the-hash, а MD4 без соли
быстро перебирается. Поэтому файл хранится вне `data/` и в облачный бэкап не едет, права у него
`0600`, запись атомарная, а NTLM предлагается только по HTTPS. Почему решение `027fd933` откатано
частично, разобрано в [conventions.md](../architecture/conventions.md#новое-хранилище--сверься-с-бэкапом).

## Итог

| Сценарий | Адрес |
|---|---|
| Дома, браузер | `http://<локальный-IP>:5000` |
| Снаружи / PWA + HTTPS | `https://<домен>.duckdns.org` (проброс + Caddy) |
| Нет белого IP (CGNAT) | туннель (Cloudflare) или Tailscale — см. раздел 2 |

Во всех случаях нужен один и тот же API-ключ.

#### Пары попыток 12:44 и состояние соединения (2026-10-06)

Попытки WebClient идут парами: первая — `GenericFailure` («NTProofStr не сходится»), вторая через 6–8 с —
`InvalidToken` (NTProofStr верен, MIC не сходится). Гипотеза «после отказа состояние Negotiate-хендлера
остаётся на соединении и следующее рукопожатие идёт поверх старого» **на стенде не подтвердилась**: после
отказа (`NegotiateFailure` + `HandleResponse`) и после брошенного Type1 без Type3 следующее рукопожатие на том
же соединении проходит (`client.py … --prefail --abandon`). Строка «Type1 записано 1» ничего не доказывает:
Type1 у Windows всегда побайтно одинаков, и рекордер его дедуплицирует, — поэтому в пробу добавлена хроника
соединения (какой Type пришёл, когда отправлен Type2, статус).

Проба расширена к следующему повтору на Windows: перебор всех записанных Type2 и Type3 в двух видах (MIC
обнулён / как есть), а при несовпадении в лог попадают Type1 и Type2 целиком (открытые сообщения рукопожатия),
длины и заголовок Type3 (первые 72 байта без ответов и ключа) и хроника соединения. Тело Type3 не выводится:
по нему и Type2 офлайн подбирается пароль.
