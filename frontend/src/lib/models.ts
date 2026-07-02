// Доступные модели для выбора в чате.
// Пустое value → флаг --model не передаётся (дефолтная модель CLI).
export interface ModelOption {
  value: string;
  label: string;
}

export const MODELS: ModelOption[] = [
  { value: '', label: 'По умолчанию' },
  { value: 'claude-opus-4-8', label: 'Opus 4.8' },
  { value: 'claude-sonnet-4-6', label: 'Sonnet 4.6' },
  { value: 'claude-haiku-4-5-20251001', label: 'Haiku 4.5' },
];

// Короткая подпись модели для отображения (id → label).
// Неизвестный id показываем как есть — например, фактическую модель из session_started.
export function modelLabel(value?: string | null): string {
  if (!value) return 'По умолчанию';
  return MODELS.find(m => m.value === value)?.label ?? value;
}

// Размер контекстного окна модели (токены) для индикатора заполнения.
// Матч по подстроке: фактический id из session_started (claude-opus-4-8-...)
// не совпадает с алиасом из MODELS.
//   Opus 4.6+/Sonnet 4.6+/Fable 5 — 1M; Haiku 4.5 — 200k; старые модели — 200k.
// Порядок важен: конкретные (haiku) раньше общих. ВАЖНО: это спека модели;
// эффективное окно, от которого claude CLI считает авто-компакт, может быть
// меньше (200k) — если проценты разойдутся с реальным компактом, свериться.
export const DEFAULT_CONTEXT_WINDOW = 200_000;
const CONTEXT_1M = 1_000_000;

const CONTEXT_WINDOWS: Array<{ match: RegExp; window: number }> = [
  { match: /haiku/i, window: 200_000 },
  { match: /opus-4-(6|7|8)|opus-4\.[678]/i, window: CONTEXT_1M },
  { match: /sonnet-(4-6|5)|sonnet-4\.6/i, window: CONTEXT_1M },
  { match: /fable|mythos/i, window: CONTEXT_1M },
  // Общий фолбэк для opus/sonnet без узнаваемой версии — консервативно 200k
  { match: /opus|sonnet/i, window: 200_000 },
];

export function contextWindowFor(model?: string | null): number {
  if (!model) return DEFAULT_CONTEXT_WINDOW;
  return CONTEXT_WINDOWS.find(m => m.match.test(model))?.window ?? DEFAULT_CONTEXT_WINDOW;
}
