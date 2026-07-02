import type { ChatItem } from '../types';
import { contextWindowFor } from './models';

// Оценка заполнения контекстного окна сессии.
// Источник: usage последнего result-элемента — вход последнего хода
// (input + cacheRead + cacheCreation) ≈ текущий размер контекста.

export interface ContextEstimate {
  tokens?: number;              // ≈ текущий размер контекста; undefined — оценки нет
  window: number;               // размер окна модели
  pct?: number;                 // 0..100; undefined когда оценки нет
  fresh: boolean;               // контекст только что свернут — оценка появится после следующего хода
  level: 'normal' | 'warn' | 'danger';
  model?: string;               // фактическая модель (последний session_started)
}

// Дефолтные пороги подсветки (переопределяются per-user, см. contextPrefs)
export const DEFAULT_CTX_WARN = 65;
export const DEFAULT_CTX_DANGER = 85;

export interface CtxThresholds {
  warnPct: number;
  dangerPct: number;
}

export function estimateContext(
  items: ChatItem[],
  fallbackModel?: string | null,
  thresholds: CtxThresholds = { warnPct: DEFAULT_CTX_WARN, dangerPct: DEFAULT_CTX_DANGER },
): ContextEstimate {
  let model: string | undefined;
  let tokens: number | undefined;
  let fresh = false;

  // Один обратный проход: ищем последний «маркер» размера контекста.
  // Result компакт-хода приходит с нулевым usage — пропускаем такие.
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (!model && it.kind === 'session_started' && it.model) model = it.model;

    if (tokens === undefined && !fresh) {
      if (it.kind === 'compact_boundary') {
        fresh = true; // компакт был позже последнего содержательного хода
      } else if (it.kind === 'result' && it.usage) {
        const sum = it.usage.inputTokens + it.usage.cacheReadTokens + it.usage.cacheCreationTokens;
        if (sum > 0) tokens = sum;
      }
    }

    if (model && (tokens !== undefined || fresh)) break;
  }

  const window = contextWindowFor(model ?? fallbackModel);
  const pct = tokens !== undefined ? Math.min(100, Math.round((tokens / window) * 100)) : undefined;
  const level: ContextEstimate['level'] =
    pct === undefined ? 'normal'
    : pct >= thresholds.dangerPct ? 'danger'
    : pct >= thresholds.warnPct ? 'warn'
    : 'normal';

  return { tokens, window, pct, fresh, level, model };
}
