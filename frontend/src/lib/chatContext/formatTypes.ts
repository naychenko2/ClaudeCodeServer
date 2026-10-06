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
  chip: { main: string; sub?: string; subShort?: string; prefix?: string; tone: 'normal' | 'warn' | 'dim' };
  // Низ всплывашки; проект подставляет хост («Запомнится для картинок проекта «AI Home».»)
  footer: (projectName: string | null) => string;
  // Только для чтения (kind = readonly): пояснение вместо органов управления
  readonlyNote?: string;
  edit?: FormatEditModel;
  // Сцена фильма / предупреждение (Б4)
  locked?: { filmPath: string; aspect: string };
  mismatch?: string;
  onChange: (wish: Omit<MediaFormatWish, 'by' | 'at'>) => void;
  // Пояснение: у модели без размеров — что она рисует по умолчанию; у «Дорисовать» без пропорций — что выбрать
  hint?: string;
  // Без выбора в «Формате» запуск невозможен: хост сам открывает всплывашку, когда это становится так
  needsChoice?: boolean;
}
