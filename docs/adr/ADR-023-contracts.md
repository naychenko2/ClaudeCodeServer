# ADR-023 · Контракты (JSON)

Приложение к [ADR-023](ADR-023-turn-context.md): примеры тел `ChatContextDto`, `409 context_changed`, события
`chat_context_changed` и запусков по ревизии (КТ-3: котировка и запуск картинки и звука, `mix`, `concat`). Источник примеров для теста `ChatContextContractsTests`: он достаёт блоки ```` ```json ````
из этого файла по метке в первой строке и проверяет, что каждый десериализуется в контракт и сериализуется
обратно без потерь. Менять контракт — только вместе с примером здесь.

Правила формы: `camelCase`; `by` — строка `human` | `agent`; `ref` — непрозрачный объект владельца вида, всегда объект (не null); `role` у
основного объекта всегда `null`; `usedBy` есть только у референса, всегда массив, пустой — серый чип; поля `executor` нет
(решение Р2, «Чем» читается у провайдера вида через `DescribeExecutor`). Подписи (`label`, `version`) считает бэкенд.

## `ChatContextDto`: картинка + референсы

Основной объект — версия картинки; референсы: персонаж (`image-character`, берёт операция «edit»), файл проекта
«для Claude» (роль `null`, генераторам не вход, поэтому серый) и образец стиля, поставленный агентом.

```json dto
{
  "revision": 7,
  "primary": {
    "id": "ci_01",
    "kind": "image",
    "ref": { "threadId": "t3", "versionId": "v2" },
    "role": null,
    "by": "human",
    "addedAt": "2026-10-03T09:15:00Z",
    "label": "hero.png",
    "version": "v2",
    "thumb": "/api/projects/p1/image-editor/chats/c1/threads/t3/versions/v2/thumb",
    "missing": false
  },
  "refs": [
    {
      "id": "ci_02",
      "kind": "image-character",
      "ref": { "slug": "anya" },
      "role": "character",
      "by": "human",
      "addedAt": "2026-10-03T09:16:00Z",
      "label": "Аня",
      "version": null,
      "thumb": null,
      "missing": false,
      "usedBy": ["edit", "inpaint"]
    },
    {
      "id": "ci_03",
      "kind": "project-file",
      "ref": { "path": "notes.md" },
      "role": null,
      "by": "human",
      "addedAt": "2026-10-03T09:17:00Z",
      "label": "notes.md",
      "version": null,
      "thumb": null,
      "missing": false,
      "usedBy": []
    },
    {
      "id": "ci_04",
      "kind": "image",
      "ref": { "threadId": "t5", "versionId": "v1" },
      "role": "style",
      "by": "agent",
      "addedAt": "2026-10-03T09:18:00Z",
      "label": "palette.png",
      "version": "v1",
      "thumb": null,
      "missing": true,
      "usedBy": ["edit"]
    }
  ]
}
```

## `409 context_changed`

Ревизия клиента устарела: в теле код ошибки и свежий DTO.

```json conflict
{
  "error": "context_changed",
  "context": {
    "revision": 8,
    "primary": null,
    "refs": []
  }
}
```

## Коды отказов 400

Тело — `{ "error": <код>, "message": <текст> }`. Коды живут в `ChatContextErrors`.

| Код | Когда |
|---|---|
| `kind_unknown` | вид не зарегистрирован (вертикаль выключена или опечатка) |
| `ref_invalid` | `ref` не прошёл `Validate` провайдера |
| `role_not_accepted` | основной объект не принимает референс с такой ролью |
| `kind_not_primary` | объект этого вида не может быть основным (`image-character`, `audio-voice`, `project-file`) |
| `refs_limit` | референсов больше 16 |
| `project_local_unsupported` | локальный проект (ADR-016) |

## Событие `chat_context_changed`

`sessionId` — базовое поле `ServerMessage` (чат-владелец); тело контекста — тот же DTO.

```json event
{
  "type": "chat_context_changed",
  "sessionId": "c1",
  "context": {
    "revision": 9,
    "primary": {
      "id": "ci_10",
      "kind": "audio",
      "ref": { "threadId": "a2" },
      "role": null,
      "by": "agent",
      "addedAt": "2026-10-03T10:00:00Z",
      "label": "утро.mp3",
      "version": "v1",
      "thumb": null,
      "missing": false
    },
    "refs": []
  }
}
```

## Запуск по ревизии контекста (КТ-3)

Поле `contextRevision` (`long?`) есть в теле котировки и запуска картинки и звука, в `mix` и `concat` (ADR §Д2.1). Оно
задано — сервер берёт входы из стора по этой ревизии, а поля входов тела игнорирует; не совпала — `409 context_changed`
(тело как в блоке `conflict` выше). Запуски картинки и звука идут формой (multipart): поле `ContextRevision` формы.
`threadId` у `mix` в маршруте, `path` у `films/build` в query — значения из DTO, сервер сверяет их со стором.

### Котировка картинки: тело

С ревизией `references`, `hasCharacter`, `width`, `height` не нужны (сервер считает по стору; в записи они необязательны — по умолчанию `0`/`false`/`null`); остаются `op`, `count`,
`provider`/`model`, `hasMask`, `hasAnnotations`, `removal`.

```json image-quote-request
{
  "provider": "auto",
  "model": "auto",
  "mode": "auto",
  "op": "edit",
  "count": 2,
  "hasMask": false,
  "references": 0,
  "hasCharacter": false,
  "width": null,
  "height": null,
  "hasAnnotations": false,
  "removal": false,
  "sessionId": "c1",
  "contextRevision": 7
}
```

### Ответ `quote`: строки исполнителей

`executors` — строки «Чем» для `op` запроса; их читает строка контекста и панель (решение Р2). `group` — `auto` | `local` |
`cloud`; у строки «Авто» `sub` несёт «сейчас: …»; серая строка — `disabled: true` и `reason` вместо `sub`.
Цена — полями: `free`, `amount` + `unit` — валюта (`free` | `usd` | `credits` | `rub`; у бесплатных `amount: null`; единица тарификации поставщика `chars`/`sec`/`min` в `unit` не попадает — исполнитель переводит её в `usd`); `amount` — цена за единицу из `PriceHint` модели (за штуку, секунду и т. п., подпись `price` называет единицу), а не за весь запуск с `count`, `etaSeconds` (у локальных); `price` — готовая подпись для показа, фронт её не разбирает. `badges` — `[{label, tone}]` (`tone`: `neutral` | `good` | `warn` | `info`): RU / без RU, лицензия, «тяжёлая». Id строки — `поставщик|модель` (в id моделей бывают слеши и двоеточия), «Авто» — `auto`. Строки картинки наполняются при `ContextRevision` в запросе (2б-3), звука — этап 2б-4; без ревизии `executors` — `null`. Образец с диска: `POST …/image-editor/uploads` (multipart, поле `file`) → `201 {uploadId}`, затем `attachRef({kind: 'image', ref: {upload}})`; образец живёт в рабочей папке модуля, как задачи (TTL 7 дней).

```json image-quote
{
  "quoteId": "q_01",
  "provider": "local",
  "model": "qwen-image-edit",
  "estimate": { "amount": null, "unit": "free", "approx": false, "source": "catalog", "etaSeconds": 40, "queueLength": 0 },
  "expiresAt": "2026-10-03T11:00:00Z",
  "expectedSeconds": 40,
  "executors": [
    { "id": "auto", "group": "auto", "name": "Авто", "sub": "сейчас: локально · Qwen-Image Edit", "price": "бесплатно · ~40 с", "free": true, "amount": null, "unit": "free", "etaSeconds": 40, "badges": [{ "label": "без RU", "tone": "warn" }], "disabled": false, "locked": false, "reason": null },
    { "id": "local|qwen-image-edit", "group": "local", "name": "Qwen-Image Edit", "sub": null, "price": "бесплатно · ~40 с", "free": true, "amount": null, "unit": "free", "etaSeconds": 40, "badges": [{ "label": "без RU", "tone": "warn" }, { "label": "apache-2.0", "tone": "neutral" }], "disabled": false, "locked": false, "reason": null },
    { "id": "fal|flux-kontext", "group": "cloud", "name": "fal · FLUX Kontext", "sub": null, "price": "$0.04 / шт.", "free": false, "amount": 0.04, "unit": "usd", "etaSeconds": null, "badges": [{ "label": "RU", "tone": "good" }], "disabled": true, "locked": false, "reason": "не умеет «Изменить отмеченное» — только новая картинка" }
  ]
}
```

### Запуск картинки: поля формы

`multipart/form-data` на `POST …/image-editor/jobs`; здесь — поля формы как JSON-объект. С ревизией `source`,
`references`, `referencePaths`, `characterSlug`, `threadId`, `versionId` не нужны; остаются `quoteId`, `prompt`, `marks`,
`aspectRatio`, `matchSourceSize`. Остальные поля формы, которых нет в примере: `sourcePath`, `source`, `mask`, `annotated`, `referenceRoles`, `referencePathRoles`, `baseStepId` — клиент шлёт их по месту (маска, аннотации, роли референсов, шаг истории).

```json image-job-form
{
  "quoteId": "q_01",
  "prompt": "добавь закат",
  "marks": null,
  "aspectRatio": null,
  "matchSourceSize": true,
  "sessionId": "c1",
  "contextRevision": 7
}
```

### Котировка звука: тело

С ревизией `threadId` и `voiceKind` не нужны (выводятся из стора); остаются `operation`, `count`, `text`/`prompt`/`lyrics`,
`durationSec`, `fields`.

```json audio-quote-request
{
  "mode": "voice",
  "operation": "speak",
  "provider": null,
  "model": null,
  "count": 1,
  "voiceKind": null,
  "sessionId": "c1",
  "threadId": null,
  "text": "Привет, мир",
  "durationSec": null,
  "fields": null,
  "prompt": null,
  "lyrics": null,
  "contextRevision": 9
}
```

### Ответ `quote` звука

```json audio-quote
{
  "quoteId": "aq_01",
  "mode": "voice",
  "op": "speak",
  "provider": "local",
  "model": "qwen-tts",
  "count": 1,
  "voiceKind": null,
  "price": { "amount": null, "unit": "free", "approx": false, "source": "catalog", "eta": 20, "queueLength": 0 },
  "license": "apache-2.0",
  "heavy": false,
  "expiresAt": "2026-10-03T11:00:00Z",
  "recreateVoice": null,
  "executors": [
    { "id": "auto", "group": "auto", "name": "Авто", "sub": "сейчас: локально · Qwen3-TTS", "price": "бесплатно · ~20 с", "free": true, "amount": null, "unit": "free", "etaSeconds": 20, "badges": [{ "label": "RU", "tone": "good" }, { "label": "apache-2.0", "tone": "neutral" }], "disabled": false, "locked": false, "reason": null }
  ]
}
```

### Запуск звука: поля формы

С ревизией `threadId`, `baseVersionId`, `voice`, `referencePath`, `clipPaths` не нужны; остаются `quoteId`, `text`,
`prompt`, `lyrics`, `language`, `durationSec`, `startSec`/`endSec` (выделение на волне), `params`. Остальные поля формы, которых нет в примере: `seed`, `reference`, `clips`, `voiceModelPath`, `voiceIndexPath` — клиент шлёт их по месту.

```json audio-job-form
{
  "quoteId": "aq_01",
  "sessionId": "c1",
  "text": "Привет, мир",
  "language": "ru",
  "contextRevision": 9
}
```

### `mix` и `concat`

```json audio-mix-request
{
  "stems": [ { "role": "stem:vocals", "gainDb": 0, "muted": false } ],
  "baseVersionId": null,
  "format": "wav",
  "revision": 4,
  "contextRevision": 9
}
```

С ревизией куски `concat` — референсы роли `piece` по `addedAt`, поле `pieces` игнорируется; остаются `joint` и прочее.

```json audio-concat-request
{
  "pieces": null,
  "joint": { "kind": "crossfade", "seconds": 0.5 },
  "joints": null,
  "normalizeLoudness": true,
  "name": "склейка",
  "format": "wav",
  "folder": null,
  "contextRevision": 9
}
```
