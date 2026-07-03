// Доступные модели для выбора в чате. Актуальный список приходит с бэка
// (/api/models — сервер спрашивает claude CLI и кэширует), до загрузки или при
// ошибке — статический fallback. Паттерн стора — как у featureFlags.ts.
// Пустое value → флаг --model не передаётся (дефолтная модель CLI).

import { useSyncExternalStore } from 'react';
import { api } from './api';

export interface ModelOption {
  value: string;
  label: string;
  description?: string;
}

// Алиасы вместо конкретных версий — не протухают при выходе новых моделей
export const FALLBACK_MODELS: ModelOption[] = [
  { value: '', label: 'По умолчанию' },
  { value: 'opus', label: 'Opus' },
  { value: 'sonnet', label: 'Sonnet' },
  { value: 'haiku', label: 'Haiku' },
];

// Подписи для id, сохранённых в старых сессиях (их нет в динамическом списке CLI)
const LEGACY_LABELS: Record<string, string> = {
  'claude-opus-4-8': 'Opus 4.8',
  'claude-sonnet-4-6': 'Sonnet 4.6',
  'claude-haiku-4-5-20251001': 'Haiku 4.5',
};

let _models: ModelOption[] = FALLBACK_MODELS;
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Загрузить список с сервера (вызывается при старте после проверки auth).
// value 'default' у CLI означает «модель по умолчанию» — маппим в '' (не передавать --model).
export async function loadModels(): Promise<void> {
  try {
    const res = await api.models.list();
    const opts: ModelOption[] = res.models.map(m => ({
      value: m.value === 'default' ? '' : m.value,
      label: m.value === 'default' ? 'По умолчанию' : m.displayName,
      description: m.description ?? undefined,
    }));
    if (opts.length > 0) {
      _models = opts;
      emit();
    }
  } catch {
    // сервер недоступен/ошибка — остаёмся на fallback
  }
}

export function getModels(): ModelOption[] {
  return _models;
}

export function subscribeModels(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Реактивный список моделей для компонентов
export function useModels(): ModelOption[] {
  return useSyncExternalStore(subscribeModels, getModels, getModels);
}

// Реактивная подпись модели: ре-рендер, когда динамический список догрузился
export function useModelLabel(value?: string | null): string {
  useModels();
  return modelLabel(value);
}

// Короткая подпись модели для отображения (id → label).
// Неизвестный id показываем как есть — например, фактическую модель из session_started.
export function modelLabel(value?: string | null): string {
  if (!value) return 'По умолчанию';
  return _models.find(m => m.value === value)?.label
    ?? FALLBACK_MODELS.find(m => m.value === value)?.label
    ?? LEGACY_LABELS[value]
    ?? value;
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
