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

### InvalidToken: причина, патчи, пакет (итог 2026-10-06)

Статус в `NTLM отклонён: …: <статус>` различает причины: `GenericFailure` — не сошёлся NTLMv2-ответ
(пароль, регистр или написание домена); `InvalidToken` — gss-ntlmssp вернул `GSS_S_DEFECTIVE_TOKEN`
после успешной проверки хэша (MIC, channel bindings или разбор AV-пар). Строка отказа также несёт
транспорт (сырой NTLMSSP или SPNEGO) и форму Type3: флаги, `MsvAvFlags`, `MsvAvChannelBindings`,
`MsvAvTargetName`. Секретов там нет. ASP.NET Negotiate на Linux channel bindings в GSSAPI не передаёт,
а по HTTP/2 хендлер Negotiate не аутентифицирует вовсе — до gss-ntlmssp Windows доходит по HTTP/1.1.

**Причина.** Два дефекта gss-ntlmssp 1.2.0 против Windows 11 24H2 (Mini-Redirector, Word):

1. Ключ сессии расшифровывался по одному флагу `KEY_EXCH`, хотя по MS-NLMP 3.1.5.1.2 / 3.2.5.1.2 нужен
   `KEY_EXCH` вместе с `SIGN` или `SEAL`. Клиент с Type1 `0xE2088207` слал KeyExchangeKey, сервер
   получал чужой ключ — MIC не сходился.
2. Type2 всегда нёс `MsvAvFlags=0`. Windows SSPI правит эту пару в своей копии CHALLENGE_MESSAGE на месте
   (`|= MIC_PRESENT`) и считает MIC по изменённому Type2, поэтому подпись не сходилась. Без этой пары
   Windows считает MIC по Type2 с провода, как и клиенты по спецификации.

**Патчи** (проверка MIC не ослаблена, меняется только выбор ключа и состав Type2):
[ntlm-key-exch-requires-sign-seal.diff](../../deploy/gss-ntlmssp/ntlm-key-exch-requires-sign-seal.diff) (`+ccs1`) и
[ntlm-no-empty-msvavflags-in-type2.diff](../../deploy/gss-ntlmssp/ntlm-no-empty-msvavflags-in-type2.diff) (`+ccs2`).
Пакет `1.2.0-1build5+ccs2` (Ubuntu 26.04) собирает [build-deb.sh](../../deploy/gss-ntlmssp/build-deb.sh) в контейнере;
готовый .deb лежит в `deploy/gss-ntlmssp/out/` (в git не коммитится), тесты матрицы флагов — `build-deb.sh --test`.
Пакет правит только акцептор (`gss_sec_ctx.c`). `+ccs1` Windows не пускает: нужен `+ccs2` или новее.

**Установка (делает человек, на хосте с боевым `ccs.service`).** Пакет:
`/home/an/ccs-packages/gss-ntlmssp_1.2.0-1build5+ccs2_amd64.deb`, sha256
`ea89f8576e721462da21e49c8801fa56a9cec7b4a9bc28eb4ebb512b1ec426e6` (`gssntlmssp.so` внутри — sha256
`3f2c5b9803469985a801663eca9f7819918122cc8501e331e4ef2ab73f38ed97`; повторная сборка даёт ту же `.so`, но другой
sha256 самого `.deb`).

```bash
sha256sum gss-ntlmssp_1.2.0-1build5+ccs2_amd64.deb   # сверить с суммой выше
sudo dpkg -i gss-ntlmssp_1.2.0-1build5+ccs2_amd64.deb
sudo apt-mark hold gss-ntlmssp
sudo systemctl restart ccs.service
deploy/gss-ntlmssp/check-gss-ntlmssp.sh        # exit 0: патч и hold на месте
```

**Проверка.** Живой Windows (curl.exe `--negotiate`, Word по WebDAV) и бой 2026-10-06: `200`, в логе
`WebDAV: NTLM-вход WORKGROUP\andrey`, неверный пароль — `401`. Воспроизведение без боевого сервера —
дев-стенд [test/stand](../../deploy/gss-ntlmssp/test/stand) и клиент [client.py](../../deploy/gss-ntlmssp/test/client.py);
если на Windows нужны полные токены тестовой учётки, стенд печатает их сам (`STAND_DUMP=1`).
Трассировка gss-ntlmssp — `Environment=GSSNTLMSSP_DEBUG=/tmp/gssntlm.log` в юните (после проверки убрать).

**Сторож отката.** `apt upgrade` без hold молча вернёт оригинал. Защиты две: hold и проверка
[check-gss-ntlmssp.sh](../../deploy/gss-ntlmssp/check-gss-ntlmssp.sh) (exit 1 — версия без `+ccs2` или новее, exit 2 —
нет hold; печатает `WARNING` в stderr; добавьте вызов в `/opt/ccs/check-release.sh` или cron). Плюс сервер при
старте пишет warning `gss-ntlmssp … без патча '+ccs2' или новее` (`NtlmUserFile`), если читает
`/var/lib/dpkg/status` и версия без суффикса.

**Откат:** `sudo apt-mark unhold gss-ntlmssp && sudo apt install --reinstall gss-ntlmssp=1.2.0-1build5`
(при отсутствии версии в индексе — `sudo apt install --reinstall gss-ntlmssp`), затем
`sudo systemctl restart ccs.service`. Без патчей NTLM у Windows снова даст InvalidToken, Basic продолжит работать.

**Upstream.** Текст issue и PR — [upstream-issue-and-pr.md](../../deploy/gss-ntlmssp/upstream-issue-and-pr.md),
не опубликован: отправка только по явной просьбе.

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
