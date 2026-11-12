// Типы домена «Генерация картинок» (переехали из types/index.ts, S5-P3). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// === Генерация картинок (иконка проекта, аватар персоны) ===
// Настройка инстанса: какой генератор идёт в работу (auto | fal | glif) и какой моделью.
// Ответ GET/PUT /api/image-generation одинаковый — после записи перечитывать не нужно.

// Расширенные возможности модели. Сервер добавляет их по мере поддержки драйверами,
// поэтому ВСЕ поля опциональны: ответ без caps валиден и означает «драйвер не сообщил».
export interface ImageModelCaps {
  // Генерация с опросом статуса (glif): картинка приезжает не в ответе запроса,
  // а догоняющим обновлением — вызывающему нужно уметь ждать
  async?: boolean;
  // Сколько вариантов драйвер отдаёт за один запрос
  maxCount?: number;
  // Поддерживаемые пропорции («1:1», «16:9»)
  aspectRatios?: string[];
}

// Модель генератора для пикера. id уходит драйверу как есть (у fal — endpoint_id, у glif — id глифа)
export interface ImageModelInfo {
  id: string;
  displayName: string;
  description?: string | null;
  caps?: ImageModelCaps | null;
}

export interface ImageGeneratorInfo {
  key: string;                  // 'fal' | 'glif'
  displayName: string;
  enabled: boolean;             // настроен (есть ключ/токен) и может генерировать
  models: ImageModelInfo[];     // пустой список — драйвер всегда идёт на свой дефолт
}

// Строка настройки одного МЕСТА генерации: у иконки проекта и аватара персоны
// свой генератор и своя модель (ключи мест — ImagePlaces.All на бэке)
export interface ImagePlaceSettings {
  key: string;                       // 'project-icon' | 'persona-avatar'
  title: string;                     // человекочитаемое название места с бэка
  provider: string;                  // режим места: 'auto' | ключ провайдера
  activeProvider: string | null;     // кто пойдёт следующим запросом; null — генерация недоступна
  enabled: boolean;                  // activeProvider !== null
  model: string | null;              // эффективная модель активного провайдера
  models: Record<string, string | null>;   // ключ провайдера → эффективная модель (для пикера)
}

export interface ImageGenerationSettings {
  providers: ImageGeneratorInfo[];
  places: ImagePlaceSettings[];
}

// Патч места: поле не прислали — оставить как есть, "" — сброс к слою ниже (конфиг → дефолт)
export interface ImagePlacePatch {
  provider?: string;
  models?: Record<string, string | null>;
}

// PUT: места, которых нет в places, не трогаются; пустой places — 400
export interface ImageGenerationPatch {
  places: Record<string, ImagePlacePatch>;
}
