// Типы домена «Карта плана» (переехали из types/index.ts, S5-P3). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// === Карта плана (часть B фичи «Визуальный разворот плана») ===
// Структурный слепок markdown-плана для разворота схемой. Зеркало Models/PlanMap.cs
// на бэке — контракт общий. Строится по кнопке «Собрать схему» через POST /api/plans/map.
// Раздел текста и блок схемы — одна сущность: Anchor + AnchorIndex клеят блок к
// заголовку раздела через useHeadings (пара текст+occurrence), и замечание на блоке
// в схеме и замечание на заголовке в тексте — одно и то же.

// Жанр плана: один из белого списка. Неизвестное значение с бэка → "feature".
export type PlanMapGenre = 'feature' | 'fix' | 'choice' | 'audit' | 'framework' | 'operation';

// Тип блока: семантика роли блока в схеме.
export type PlanMapBlockType = 'step' | 'decision' | 'fork' | 'risk' | 'criterion' | 'boundary';

// Флаг внимания: блок несёт причину вернуться к нему. На «Сути» показываются только блоки
// с непустыми флагами. Потолок числа флагов — на СЕРВЕРЕ (см. PlanMapService.MaxFlaggedBlocks):
// фронт НЕ повторяет правило (две копии правила = дефект, тот же довод, по которому признак
// архива чата считает сервер и отдаёт готовым). Неизвестный флаг с бэка отбрасывается молча.
export type PlanMapBlockFlag =
  | 'blocking'
  | 'needs-decision'
  | 'expands-scope'
  | 'has-cost'
  | 'review-fix';

export interface PlanMapNumber {
  value: string;       // «3», «5 КБ», «2 недели»
  label: string;       // «шага», «файлов затрагивается»
}

export interface PlanMapBlock {
  id: string;          // стабильный id, на него ссылаются dependsOn
  title: string;       // заголовок блока одной строкой (не путать с якорем)
  type: PlanMapBlockType;
  flags: PlanMapBlockFlag[];
  // ТОЧНЫЙ текст заголовка раздела плана. Блок с якорем, которого нет среди заголовков
  // плана, отбрасывается на бэке — фронт повторно фильтрует (см. PlanScheme.resolveAnchor)
  anchor: string;
  // 0-based номер вхождения заголовка среди одноимённых. КЛЕИТ блок к РАЗДЕЛУ, не к
  // первому совпадению по тексту: при двух «Дизайн» в плане идём во второе, а не в первое.
  anchorIndex: number;
  dependsOn: string[]; // id блоков, которые должны закрыться раньше
}

export interface PlanMap {
  genre: PlanMapGenre;
  oneLine: string;     // суть плана одной фразой
  numbers: PlanMapNumber[];
  blocks: PlanMapBlock[];
}
