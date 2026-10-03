# ADR-023 · Контракты (JSON)

Приложение к [ADR-023](ADR-023-turn-context.md): примеры тел `ChatContextDto`, `409 context_changed` и события
`chat_context_changed`. Источник примеров для теста `ChatContextContractsTests`: он достаёт блоки ```` ```json ````
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
