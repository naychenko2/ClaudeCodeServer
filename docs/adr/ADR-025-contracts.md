# ADR-025: контракты «Формата»

Приложение к [ADR-025](ADR-025-media-format.md). JSON — camelCase, строковые enum'ы, `null`-поля опущены. Раздел «Контракты» ниже **проверяется тестом** (`MediaFormatContractExamplesTests`): каждый пример десериализуется в тип из заголовка и сериализуется обратно в тот же JSON; у каждого типа в тесте обязан быть пример.

Ручка: `GET /api/projects/{id}/image-editor/format?provider=…&model=…&op=…` (и личная область `/api/image-editor/format`) — ответ `MediaFormatState`. У видео будет та же форма (Б2). Префы: `PUT …/image-editor/prefs` с полем `format` (`MediaFormat`); запись агента ставит `by: "agent"`, любая ручная — `"human"`.

## Не проверяется тестом (форма фронта и тело запуска)

`FormatModel` для чипа — то, что вертикаль отдаёт общему компоненту; строится из `MediaFormatState` без арифметики:

```ts
interface FormatModel {
  title: string                    // «Формат новой картинки», «Формат · кот.png», «Формат сцены»
  state: MediaFormatState          // ответ ручки как есть
  by: 'human' | 'agent'            // ✦ на чипе при 'agent'
  locked?: { filmPath: string; aspect: string }   // сцена фильма, 🔒 (Б2)
  mismatch?: string                // предупреждение о подрезке или о формате фильма (Б2, Б4)
  editChoice?: boolean             // правка: нужен выбор «Дорисовать / Обрезать» (Б3)
  onChange(wish: MediaFormat | null): void
}
```

Тело запуска картинки (`POST …/image-editor/jobs`) получает необязательное поле `format` — желание одной правки, в префы оно не пишется (запись — `PUT prefs`):

```json
{ "op": "generate", "prompt": "…", "provider": "fal", "model": "fal-ai/flux-pro/v1.1",
  "count": 1, "format": { "mode": "aspect", "aspect": "16:9", "tier": "high" } }
```

Обрезка (`POST /api/projects/{id}/image-editor/sessions/{sessionId}/threads/{threadId}/crop`, в личном чате — `/api/image-editor/chats/{sessionId}/threads/{threadId}/crop`; тот же метод сервиса `ImageThreadService.CropToAspectAsync`, что у `image_generate op = crop`): тело `{ "aspect": "1:1", "anchor": "left", "versionId": null, "dryRun": false }`; ответ `{ width, height, aspect, versionId, state }` (`state` — нити чата), при `dryRun: true` версия не создаётся, `versionId = null`. Лента: запись `image_launch_versions` с `provider: "none"`, `model: "crop"`, `crop {aspect, anchor, width, height}` и текстом «Вы обрезали без ИИ: 1200×1200 · 1:1 · бесплатно · версия 3» (у агента — «✦ Claude обрезал без ИИ: …»).

Тихая строка агента при смене формата: запись `image_format` `{ by: "agent", before, after }`, текст «✦ Claude сменил формат картинок проекта: 16:9 → 9:16».

## Контракты

#### MediaFormat

```json
{
  "mode": "aspect",
  "aspect": "16:9",
  "tier": "high",
  "by": "agent",
  "at": "2026-10-06T12:00:00Z"
}
```

#### MediaFormat (exact)

```json
{
  "mode": "exact",
  "width": 1920,
  "height": 1080,
  "by": "human",
  "at": "2026-10-06T12:05:00Z"
}
```

#### FormatTierSpec

```json
{
  "tier": "max",
  "label": "1440p",
  "sizes": {
    "16:9": { "w": 2528, "h": 1440 },
    "9:16": { "w": 1440, "h": 2528 }
  },
  "wireValue": "1440p",
  "priceFactor": 1,
  "postStep": "upscale"
}
```

#### ExactSizeSpec

```json
{ "step": 64, "minSide": 256, "maxSide": 2048, "maxMegapixels": 4 }
```

#### MediaFormatCaps

```json
{
  "modelLabel": "FLUX",
  "aspects": ["1:1", "16:9", "9:16"],
  "tiers": [
    {
      "tier": "standard",
      "label": "1 Мп",
      "sizes": { "1:1": { "w": 1024, "h": 1024 }, "16:9": { "w": 1344, "h": 768 }, "9:16": { "w": 768, "h": 1344 } },
      "wireValue": "1",
      "priceFactor": 1
    },
    {
      "tier": "high",
      "label": "2 Мп",
      "sizes": { "1:1": { "w": 1408, "h": 1408 }, "16:9": { "w": 1792, "h": 1024 }, "9:16": { "w": 1024, "h": 1792 } },
      "wireValue": "2",
      "priceFactor": 2
    }
  ],
  "exact": { "step": 64, "minSide": 256, "maxSide": 2048 },
  "wireParam": "megapixels"
}
```

#### ResolvedFormat

```json
{
  "mode": "aspect",
  "aspect": "3:2",
  "tier": "standard",
  "width": 1536,
  "height": 1024,
  "substituted": true,
  "rounded": false,
  "note": "16:9 нет у GPT Image — взяли 3:2. У GPT Image другие ступени: «Высокое» — 1 Мп."
}
```

#### FormatTierOption

```json
{
  "tier": "high",
  "label": "1792×1024",
  "size": { "w": 1792, "h": 1024 },
  "wireValue": "2",
  "price": { "amount": 0.08, "unit": "usd", "approx": false }
}
```

#### FormatTierOption (серая)

```json
{
  "tier": "max",
  "label": "нет у модели",
  "disabledReason": "Модель рисует один размер ~1,6 Мп — «Высокое» и «Макс» у неё недоступны"
}
```

#### MediaFormatState

```json
{
  "caps": {
    "modelLabel": "GPT Image",
    "aspects": ["1:1", "3:2", "2:3"],
    "tiers": [
      {
        "tier": "standard",
        "label": "1 Мп",
        "sizes": { "1:1": { "w": 1024, "h": 1024 }, "3:2": { "w": 1536, "h": 1024 }, "2:3": { "w": 1024, "h": 1536 } },
        "priceFactor": 1
      }
    ],
    "nativeNote": "один из трёх размеров около 1,6 Мп"
  },
  "wish": {
    "mode": "aspect",
    "aspect": "16:9",
    "tier": "high",
    "by": "human",
    "at": "2026-10-06T12:00:00Z"
  },
  "resolved": {
    "mode": "aspect",
    "aspect": "3:2",
    "tier": "standard",
    "width": 1536,
    "height": 1024,
    "substituted": true,
    "rounded": false,
    "note": "16:9 нет у GPT Image — взяли 3:2. У GPT Image другие ступени: «Высокое» — 1 Мп."
  },
  "tiers": [
    {
      "tier": "standard",
      "label": "1 Мп",
      "size": { "w": 1536, "h": 1024 },
      "price": { "amount": 0.04, "unit": "usd", "approx": false }
    },
    {
      "tier": "high",
      "label": "нет у модели",
      "disabledReason": "Модель рисует один из трёх размеров около 1,6 Мп — «Высокое» и «Макс» у неё недоступны"
    },
    {
      "tier": "max",
      "label": "нет у модели",
      "disabledReason": "Модель рисует один из трёх размеров около 1,6 Мп — «Высокое» и «Макс» у неё недоступны"
    }
  ],
  "exactUnavailableReason": "«Точно…» недоступно: GPT Image рисует только три размера — 1024×1024, 1536×1024 и 1024×1536."
}
```
