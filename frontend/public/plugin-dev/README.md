# Хост-эмулятор панелей плагинов

Инструмент разработки (шаг A2 плана «Погода»): собирает и гоняет панель плагина в
браузере **без установки**. Панель живёт ровно в той же среде, что вживую:
двухуровневый фрейм, `PANEL_CSP`, шим `window.ccsPlugin` из `plugin-kit.js`,
структурный CSS кита и токены `--cc-*` текущей темы. Эмулятор — **общий инструмент**:
панель, права и фикстуры задаются параметрами URL, ничего «погодного» в его код не
вшито. В пакет плагина он не попадает (allow-list пакета — только `panel/**` и
манифесты).

Сборка документа панели — копия логики хоста (`panelSrcdoc.ts`, `panelTokens.ts`,
`bridge.ts`); платформенный код эмулятор не трогает.

## Запуск

Из корня репозитория, без сборки:

```bash
python3 -m http.server 5000
```

Открыть:

```
http://localhost:5000/frontend/public/plugin-dev/index.html?panel=/frontend/public/plugin-dev/weather/panel/index.html
```

На дев-стенде фронт отдаёт `frontend/public/` из корня, поэтому там тот же эмулятор
доступен как `/plugin-dev/`.

## Переключатели (в URL или в панели управления)

| Парамет | Значения | Что делает |
|---|---|---|
| `panel=` | путь к HTML панели | какую панель монтировать (по умолчанию — заглушка «Погоды» из `weather/panel/`) |
| `grant=` | хосты через запятую, например `api.open-meteo.com,geocoding-api.open-meteo.com` | выданные права `net:{host}`; пусто — ни одного права. `ccsPlugin.state.granted` совпадает с этим списком |
| `fail=` | `upstream` / `timeout` / `limit` / `status400` | смоделировать сбой: коды и тексты — как у сервера; `status400` приходит как **успех** `net.fetch` с `status: 400` (графль §1 плана) |
| `delay=` | мс | задержка ответа `net.fetch` — чтобы увидеть скелетон панели |
| `fixtures=` | пути к файлам через запятую | не выходить в сеть: ответ берётся из файла-фикстуры. Одна — для всех вызовов; несколько — по порядку вызовов |
| `theme=` | `light` / `dark` | тема хоста; переключается кнопкой без перезагрузки — панель перекрашивается сообщением `theme`, как вживую |
| `width=` | `rail` / `320` / `390` | ширина карточки: свободная (как рельс) или фиксированная мобильная |
| `selftest=` | `1` | панель сама прогоняет тестовые вызовы (прогноз, геокодер, прямой `fetch`) и пишет результат в лог эмулятора и в `window.__emulator.selftest` |

Пример полного прогона «Погоды» на фикстурах (данные — `tools/plugins/weather/fixtures/`):

```
http://localhost:5000/frontend/public/plugin-dev/index.html?panel=/frontend/public/plugin-dev/weather/panel/index.html&selftest=1&grant=api.open-meteo.com,geocoding-api.open-meteo.com&fixtures=/tools/plugins/weather/fixtures/forecast-yaroslavl.json,/tools/plugins/weather/fixtures/geocode-yaroslavl.json
```

## Что и как проверять

1. **Права.** Открыть с `grant=api.open-meteo.com,geocoding-api.open-meteo.com`,
   `grant=api.open-meteo.com`, `grant=` (пусто). В консоли фрейма:
   `ccsPlugin.state.granted` — ровно то, что в `grant=`. Вызов
   `ccsPlugin.call('net.fetch', {url:'https://geocoding-api.open-meteo.com/v1/search?name=%D0%AF%D1%80%D1%86&count=10&language=ru&format=json'})`
   при `grant=api.open-meteo.com` (право на геокодер не выдано) даёт `denied` с текстом
   `Нет права: net:geocoding-api.open-meteo.com` и плашку «Нет права …» над панелью;
   при пустом `grant=` — `Нет права: net:` (отказ на уровне моста, без хоста).
2. **Данные.** С `fixtures=` вызовы возвращают фикстуры без сети; без него —
   реальный `window.fetch` хоста.
3. **Сеть закрыта.** Прямой `fetch(...)` из консоли фрейма падает: CSP панели —
   `connect-src 'none'`, весь выход наружу — только мостом.
4. **Тема.** Кнопка «тема» перекрашивает панель без перезагрузки: шим переписывает
   `style#cc-tokens` (first-paint уже в цвете текущей темы).
5. **Высота.** Фрейм растягивается по `resize` панели (кламп 48–20000 px), как хост.
6. **Лог.** Каждый `call` и ответ видны в логе под карточкой — по нему видно,
   сколько вызовов сделала панель (например, ровно 2 на поиск города).
