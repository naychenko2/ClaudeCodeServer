# browser-spike — спайк браузерного канала (ADR-008)

Прототип для проверки гипотезы «DOM-канал снимает тихую подмену цели, которую даёт
UIA на виртуализированных списках». Отчёт — [docs/research/browser-channel-spike.md](../../docs/research/browser-channel-spike.md).
Вне основного решения; зависимостей нет (чистый Node, как MCP-серверы продукта).

## Состав

| Путь | Что |
|---|---|
| `measure.mjs` | замеры прямым CDP (те же команды, что пойдут через `chrome.debugger`): источники среза, пары «срез → действие → срез», iframe |
| `normalize.mjs` | разбор `DOMSnapshot.captureSnapshot` (формат Chrome 128+: общие `strings`, вложенные `bounds`, per-node `attributes`) → плоские узлы с `backendNodeId` |
| `analyze.mjs` | офлайн-метрики по `results/raw/*.jsonl`: правило ADR-008 против oracle `backendNodeId` (аналог `RuntimeId` из части 2 UIA) |
| `drive.mjs` | пульт драйверного браузера (Chrome for Testing) для замера 5 |
| `swlife.mjs` | замер 5: живучесть service worker + `connectNative` под нагрузкой и в тишине |
| `cdp-eval.mjs` | утилита: выполнить выражение в CDP-таргете по подстроке url |
| `extension/` | MV3-расширение (background SW + popup): срезы через `chrome.debugger`, native messaging к хосту |
| `host/` | native messaging host (`host.bat` → Node): пишет срезы в `results/raw/*.jsonl`, отвечает pong; `install.ps1 -ExtensionId <id>` — регистрация в HKCU |
| `fixtures/serve.mjs` | два origin: `127.0.0.1:8801` (A) и `:8802` (B); `/uia/*` — фикстуры части 1 спайка UIA |
| `fixtures/` | `page-vlist-windowed.html` (настоящая DOM-виртуализация), `page-iframe.html` + inner (cross-origin) |

## Замеры 1–4, 6 (прямой CDP)

```powershell
node fixtures/serve.mjs                    # оба origin (держать запущенным)
# драйверный branded Chrome: отдельный профиль (запрет CDP касается только дефолтного)
& "C:\Program Files\Google\Chrome\Application\chrome.exe" --user-data-dir="$env:TEMP\ccs-spike-profile" --remote-debugging-port=9333 --no-first-run about:blank

node measure.mjs sources "http://127.0.0.1:8801/uia/page-simple.html" simple
node measure.mjs pairs   "http://127.0.0.1:8801/uia/page-vlist.html" vlist "control,scroll3_s1,scroll3_s2,scroll3_s3"
node measure.mjs pairs   "http://127.0.0.1:8801/page-vlist-windowed.html" windowed "control,scroll3_s1"
node measure.mjs pairs   "http://127.0.0.1:8801/uia/page-simple.html" simple "control,tabswitch,resize"
node measure.mjs pairs   "http://127.0.0.1:8801/uia/page-vlist.html" vlist_render "control,rerender_soft,rerender_hard"
node measure.mjs iframe  "http://127.0.0.1:8801/page-iframe.html"
node analyze.mjs                          # свести метрики в results/summary.md
```

Шаги pairs: `control | scroll3_N | resize | tabswitch | rerender_soft | rerender_hard`.
Синтетический ввод доходит только в выведенную на передний план вкладку — `measure.mjs`
сам делает `Page.bringToFront` и разворачивает окно.

## Замер 5 (расширение + native messaging)

Branded Chrome не годится: `--load-extension` игнорируется с Chrome 137, а автоматической
установки unpacked-расширения нет (подробности и 5 проверенных путей — в отчёте, раздел
«Дистрибуция»). Поэтому драйвер — **Chrome for Testing** (движок и MV3 те же):

```powershell
# скачать и распаковать chrome-win64 (googlechromelabs.github.io/chrome-for-testing)
node drive.mjs launch <путь>\chrome-win64\chrome.exe
node drive.mjs wait-sw 1                                   # SW расширения на связи
# узнать фактический ID расширения (без "key" в манифесте он зависит от пути):
curl -s http://127.0.0.1:9335/json/list                    # url service_worker-таргета
powershell -File host/install.ps1 -ExtensionId <id>        # allowed_origins + HKCU
node drive.mjs kill; node drive.mjs launch <путь>\chrome.exe   # рестарт: манифест хоста читается при старте
node swlife.mjs                                            # нагрузка 2 мин + тишина 45 с + контроль
node drive.mjs stats                                       # счётчики канала из SW
powershell -File host/uninstall.ps1                        # убрать регистрацию после замеров
```

Важно: `key` в манифесте unpacked-расширения ломает загрузку (диалог «Ошибка загрузки
расширения», Chrome/CfT 151–152) — поэтому ID берётся фактическим, а не детерминируется
ключом. CfT из папки репозитория требует `--no-sandbox` (sandbox не стартует: «Отказано
в доступе»).

## Источники среза

- **domsnapshot** — `DOMSnapshot.captureSnapshot` (CDP): `backendNodeId`, rect, путь, атрибуты, текст, все фреймы включая OOPIF;
- **ax** — `Accessibility.getFullAXTree` (CDP): роль+имя по `backendNodeId`, без геометрии, main frame only;
- **content** — обход DOM из content script / `Runtime.evaluate`: всё, кроме `backendNodeId`.

Единая модель для метрик — join domsnapshot+ax по `backendNodeId`. Сырые срезы пишутся в
`results/raw/` (вне git), сводка — `results/summary.md`.
