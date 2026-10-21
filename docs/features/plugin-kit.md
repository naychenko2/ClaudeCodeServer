# Kit панели плагина (plugin-kit)

> Справочник для авторов панелей: API шима `ccsPlugin`, правило высоты, набор классов и шрифт.
> Протокол моста и инварианты — [ADR-031 §3](../adr/ADR-031-plugins.md), устройство фичи —
> [plugins.md в architecture](../architecture/plugins.md). Панель — недоверенный код в
> sandbox-iframe (без `allow-same-origin`): единственный канал к хосту — мост `window.ccsPlugin`.

## Что это

Панель плагина — один самодостаточный HTML (скрипты и стили инлайн, потолок 256 КиБ на файл).
Хост встраивает в его `<head>` шим `plugin-kit` (мост + токены тем) и CSP, а сам HTML панели
кладёт в `<body>` (ADR-031 §3.6). Панель не видит DOM, хранилища и JWT хоста; сеть из кадра
закрыта CSP (`connect-src 'none'`) — весь выход наружу только мостом.

## API шима `ccsPlugin`

Хост встраивает шим перед `<body>`; объект `window.ccsPlugin` — единственный канал к хосту.

| Метод | Смысл |
|---|---|
| `ccsPlugin.ready()` | Шим загрузился. Шим шлёт его сам при загрузке — хост отвечает `init`. Повторный `ready` игнорируется |
| `ccsPlugin.call(op, args)` | Вызов операции хоста. Возвращает `Promise`: данные на успех, `{code, message}` на сбой |
| `ccsPlugin.resize(height)` | Высота контента панели (px); шим шлёт её сам, вручную звать не нужно |
| `ccsPlugin.on(type, cb)` | Подписка на `init` / `theme` / `context`; возвращает функцию отписки |
| `ccsPlugin.state` | Текущий снимок `{plugin, slot, context, theme, locale, granted}` — читать можно в любой момент, не только из `on()` |

Шим дополнительно диспатчит DOM-события `ccs:init` / `ccs:theme` / `ccs:context` на `window`
(detail — те же данные): панель, которая не использует `on()`, может слушать их через
`addEventListener`.

### Сообщения хоста

| type | Поля | Смысл |
|---|---|---|
| `init` | `plugin: {id, version}`, `slot`, `context`, `theme`, `locale`, `granted` | Ответ на `ready`: кто вы, в каком слоте, какие права |
| `result` | `id`, `ok: true, data` или `ok: false, error: {code, message}` | Итог `call` с тем же `id` |
| `theme` | `theme` | Смена темы без перемонтирования |
| `context` | `context` | Смена контекста слота (например, другой проект) |

`granted` — информационная копия выданных прав, чтобы панель прятала недоступное. **Источник
правды — сервер**: панель, совравшая себе про права, получит `denied` на вызове.

### Операции и коды ошибок

Операции называются `{область}.{глагол}`; каждая требует ровно одного права (ADR-031 §3.4). MVP:

| op | Право | args |
|---|---|---|
| `notes.search` | `notes:read` | `{query, limit?}` |
| `notes.read` | `notes:read` | `{id}` |
| `tasks.list` | `tasks:read` | `{status?, limit?}` в пределах проекта контекста |
| `net.fetch` | `net:{host}` (+ `secret:{name}` при `auth`) | `{url, method?, headers?, body?, auth?}` |

`call` резолвится данными на успех и `{code, message}` на сбой. Коды:

| code | Смысл |
|---|---|
| `denied` | Нет выданного права (или подсистема/локальный проект) |
| `not_found` | Нет установки или объекта |
| `invalid` | Не тот формат args, неизвестный `op` |
| `unavailable` | Подсистема или фича выключены |
| `upstream` | Сбой внешнего хоста у `net.fetch` |
| `timeout` | Превышен таймаут |
| `limit` | Превышен потолок размера или числа вызовов в полёте |

## Правило высоты

**Не задавай высоту `body` и не используй `100vh`.** Прокрутка — у хоста, а не у фрейма:
фрейм растягивается по контенту. Хост не видит DOM фрейма (sandbox), поэтому шим сам измеряет
`body.scrollHeight` через `ResizeObserver` и шлёт `resize` с высотой контента; хост растягивает
фрейм под неё (потолок 20 000px: длиннее — разбивай на страницы, хвост отрезается) и прокручивает
панель сам, своей полосой. Панель раскладывает контент вертикально и не рисует собственный скролл.
Известное ограничение: при фокусе внутри панели PgDn и пробел её не прокручивают; чинить не будем.

## Набор классов

Готовые стили контролов под дизайн-систему хоста. **Классы опт-ин: голые `button`/`input`
не стилизуются** — старые плагины, которые рисуют контролы сами, не ломаются.

| Класс | Модификаторы | Назначение |
|---|---|---|
| `.cc-btn` | `--primary`, `--ghost`, `--danger`, `--sm` | Кнопка: основной / призрак / опасный / малый |
| `.cc-input` | — | Поле ввода |
| `.cc-list` / `.cc-list-item` | `.is-active` | Список; `.is-active` — выбранный элемент |
| `.cc-card` | — | Карточка-контейнер |
| `.cc-badge` | тона | Метка-бейдж |
| `.cc-empty` | `__title`, `__text` | Пустое состояние: заголовок + текст |
| `.cc-notice` | — | Уведомление |
| `.cc-skeleton` | — | Скелетон-заглушка при загрузке |

Все девять классов — готовые контролы под дизайн-систему хоста. Цвета — только
токенами `--cc-*`, которые хост доставляет панели (текущая тема, обе темы; сырого
hex нет), размеры — из шкал `FS`/`SP`/`R`; фокус — видимый `outline` в акценте,
элементы достижимы с клавиатуры, контраст — не ниже хостового. Панель, собранная
из этих классов, выглядит как часть продукта, а не как чужой виджет.

## Шрифт

Латиница — вшитый **Hanken Grotesk**; кириллица — системный фолбэк. В бесплатном Hanken
Grotesk кириллических глифов нет, и у хоста та же история — паритет: панель и хост рендерят
кириллицу одним системным шрифтом.

## Пример панели

Минимальный рабочий HTML: список результатов `notes.search` с обработкой отказа по праву.
Классы из набора, высота — по контенту (без `100vh`), отказ по праву — в `.cc-notice`.

```html
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <style>
    .cc-list-item.is-active { background: var(--cc-bg-inset); }
  </style>
</head>
<body>
  <h1>Заметки</h1>
  <input class="cc-input" id="q" placeholder="Поиск…">
  <ul class="cc-list" id="results"></ul>
  <div class="cc-empty" id="empty" hidden>
    <div class="cc-empty__title">Ничего не найдено</div>
    <div class="cc-empty__text">Попробуйте другой запрос</div>
  </div>
  <div class="cc-notice" id="notice" hidden></div>
  <script>
    const q = document.getElementById('q');
    const list = document.getElementById('results');
    const empty = document.getElementById('empty');
    const notice = document.getElementById('notice');

    async function search() {
      notice.hidden = true;
      try {
        const hits = await ccsPlugin.call('notes.search', { query: q.value, limit: 20 });
        list.innerHTML = '';
        empty.hidden = hits.length > 0;
        for (const hit of hits) {
          const li = document.createElement('li');
          li.className = 'cc-list-item';
          li.textContent = hit.title ?? hit.id;
          li.addEventListener('click', () => select(li, hit));
          list.appendChild(li);
        }
      } catch (err) {
        // Отказ по праву: панель не обязана иметь notes:read
        if (err.code === 'denied') {
          notice.textContent = 'Нет права на чтение заметок — включите его в настройках плагина.';
          notice.hidden = false;
        } else {
          notice.textContent = 'Ошибка: ' + (err.message || err.code);
          notice.hidden = false;
        }
      }
    }

    function select(el, hit) {
      for (const child of list.children) child.classList.remove('is-active');
      el.classList.add('is-active');
    }

    q.addEventListener('input', search);
  </script>
</body>
</html>
```
