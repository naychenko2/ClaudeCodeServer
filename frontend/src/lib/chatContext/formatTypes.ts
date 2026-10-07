// Контракты «Формата» на фронте (ADR-025, docs/adr/ADR-025-contracts.md): зеркало Core-типов
// Services.Media и форма FormatModel, которую вид отдаёт хосту. Арифметики здесь нет: округление,
// «ближайшее» и причины серых ступеней считает сервер (MediaFormatResolver), фронт показывает готовое.

export type FormatTierKey = 'standard' | 'high' | 'max';
export type FormatMode = 'model' | 'aspect' | 'exact' | 'source';
export type FormatActor = 'human' | 'agent';

export interface FormatSize { w: number; h: number }

export interface FormatTierSpec {
  tier: FormatTierKey;
  label: string;
  sizes: Record<string, FormatSize>;
  wireValue?: string | null;
  priceFactor?: number;
  postStep?: string | null;
}

export interface ExactSizeSpec { step: number; minSide: number; maxSide?: number | null; maxMegapixels?: number | null }

export interface MediaFormatCaps {
  modelLabel: string;
  aspects: string[];
  tiers: FormatTierSpec[];
  exact?: ExactSizeSpec | null;
  wireParam?: string | null;
  nativeNote?: string | null;
}

// Желание — единственное, что хранится (PUT …/prefs, поле format); by и время ставит сервер
export interface MediaFormatWish {
  mode: FormatMode;
  aspect?: string | null;
  tier?: FormatTierKey | null;
  width?: number | null;
  height?: number | null;
  by: FormatActor;
  at: string;
}

export interface ResolvedFormat {
  mode: FormatMode;
  aspect?: string | null;
  tier?: FormatTierKey | null;
  width?: number | null;
  height?: number | null;
  wireValue?: string | null;
  postStep?: string | null;
  substituted: boolean;
  rounded: boolean;
  note?: string | null;
}

export interface FormatPrice { amount: number; unit: string; approx?: boolean }

export interface FormatTierOption {
  tier: FormatTierKey;
  label: string;
  size?: FormatSize | null;
  wireValue?: string | null;
  postStep?: string | null;
  price?: FormatPrice | null;
  disabledReason?: string | null;
  // Подпись размера целиком вместо одного размера: у видео «720p · 1280×720» (имя ступени модели и размер)
  caption?: string | null;
  // Время ступени после цены («~21 мин»): видео знает его из котировки, у картинок не бывает
  extra?: string | null;
  // Пояснение под рядом ступеней, пока ступень выбрана («Макс» local H3: съёмка и увеличение — два прогона)
  note?: string | null;
}

// Ответ GET …/image-editor/format
export interface MediaFormatState {
  caps: MediaFormatCaps | null;
  wish: MediaFormatWish | null;
  resolved: ResolvedFormat;
  tiers: FormatTierOption[];
  exact?: ExactSizeSpec | null;
  exactUnavailableReason?: string | null;
}

export type CropAnchor = 'left' | 'center' | 'right';
// Якорь рамки подрезки кадров видео: по бокам — left/right, по высоте — top/bottom
export type FramesAnchor = CropAnchor | 'top' | 'bottom';

// Предупреждение «кадры будут подрезаны» (видео): превью исходных кадров в рамке цели и выбор якоря рамки
export interface FormatFramesModel {
  text: string;
  previews: { slot: string; aspect: string; thumb: string | null }[];
  target: string;
  anchor: FramesAnchor;
  // Ось выбора рамки: sides — «Слева · По центру · Справа» (режутся бока), height — «Сверху · По центру · Снизу»
  // (режется высота), mixed — кадры режутся в разные стороны: выбора нет, рамка по центру
  axis: 'sides' | 'height' | 'mixed';
  onAnchor: (anchor: FramesAnchor) => void;
}

// Вопрос при попытке сменить формат у сцены фильма: сервер ответил 409 film_format_locked
export interface FormatLockAsk {
  text: string;
  keepLabel: string;
  forceLabel: string;
  onKeep: () => void;
  onForce: () => void;
}

// Правка картинки: другие пропорции. Выбор «Дорисовать / Обрезать» вид держит сам, хост только рисует
export interface FormatEditModel {
  // Исходник правки; null — размер ещё не прочитан
  source: { width: number; height: number; aspect: string; thumb: string | null } | null;
  // Выбранные «другие пропорции»; null — «Как у исходника»
  aspect: string | null;
  // Выбор способа получить пропорции; null — вопрос ещё не отвечен (рисуется диалог)
  how: 'outpaint' | 'crop' | null;
  anchor: CropAnchor;
  outpaint: { detail: string; size: string | null; ready: boolean };
  crop: { detail: string; size: string | null; ready: boolean };
  onSource: () => void;
  onAspect: (aspect: string) => void;
  onHow: (how: 'outpaint' | 'crop') => void;
  onAnchor: (anchor: CropAnchor) => void;
  onCancel: () => void;
  // Сервер вернул размер результата обрезки; превью рамки рисует хост по исходнику и пропорциям
  cropping: boolean;
}

export type FormatKind = 'generate' | 'edit' | 'readonly';

export interface FormatModel {
  kind: FormatKind;
  // «Формат новой картинки», «Формат · кот.png», «Формат сцены»
  title: string;
  // Ответ ручки как есть; null — ещё грузится (чип «Формат …» без цифр)
  state: MediaFormatState | null;
  by: FormatActor | null;
  // Подпись свёрнутого чипа считает вид: правка и «Увеличить» — не из ручки. main — жирным, sub — приглушённо
  // после него, subShort — вместо sub на телефоне (пустая строка — без хвоста), prefix — серая подпись перед main
  chip: { main: string; sub?: string; subShort?: string; subBare?: string; prefix?: string; tone: 'normal' | 'warn' | 'dim' };
  // Низ всплывашки; проект подставляет хост («Запомнится для картинок проекта «AI Home».»)
  footer: (projectName: string | null) => string;
  // Только для чтения (kind = readonly): пояснение вместо органов управления
  readonlyNote?: string;
  edit?: FormatEditModel;
  // Сцена фильма: 🔒 на чипе; note — пояснение в теле («Формат фильма «…» — 16:9, его задала сцена 1…»)
  locked?: { filmPath: string; aspect: string; note?: string };
  lockAsk?: FormatLockAsk;
  // Пропорции сцены не совпадают с фильмом: жёлтый чип с ⚠ и кнопка «Привести к 16:9»
  mismatch?: string;
  mismatchAction?: { label: string; onClick: () => void };
  // Кадры A/B будут подрезаны: ⚠ на чипе, превью и якорь в теле
  frames?: FormatFramesModel;
  onChange: (wish: Omit<MediaFormatWish, 'by' | 'at'>) => void;
  // Пояснение: у модели без размеров — что она рисует по умолчанию; у «Дорисовать» без пропорций — что выбрать
  hint?: string;
  // Без выбора в «Формате» запуск невозможен: хост сам открывает всплывашку, когда это становится так
  needsChoice?: boolean;
}
