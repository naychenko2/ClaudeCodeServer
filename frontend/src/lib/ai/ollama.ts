// Клиент локального ранжирования действий (бэкенд-прокси к Ollama). Фронт собирает
// компактный контекст (contextCollect.ts) и список доступных действий, бэкенд гоняет
// через модель. Кэш по сигнатуре контекста — не дёргаем модель дважды на одно и то же.

import { request } from '../offline';
import type { SuggestionLevel } from './levels';

export interface RankedAction { id: string; level: SuggestionLevel }
export interface SuggestActionsResult { available: boolean; ranked: RankedAction[] }

interface CandidateDto { id: string; title: string; hint: string }

// --- caps: сконфигурирована ли Ollama (кэш на сессию) ---
let _capsPromise: Promise<boolean> | null = null;

export function aiOllamaAvailable(): Promise<boolean> {
  _capsPromise ??= request<{ ollama: boolean }>('/ai/caps')
    .then(c => !!c.ollama)
    .catch(() => false);
  return _capsPromise;
}

// --- ранжирование с кэшем по сигнатуре контекста ---
const _cache = new Map<string, SuggestActionsResult>();
const CACHE_MAX = 40;

function hash(s: string): string {
  let h = 0;
  for (let i = 0; i < s.length; i++) { h = (h * 31 + s.charCodeAt(i)) | 0; }
  return h.toString(36);
}

function sigOf(type: string, text: string, actions: CandidateDto[]): string {
  return `${type}|${hash(text)}|${actions.map(a => a.id).join(',')}`;
}

// Вернуть кэшированный результат по сигнатуре, если есть (для мгновенной реакции FAB).
export function cachedSuggestion(type: string, text: string, actions: CandidateDto[]): SuggestActionsResult | null {
  return _cache.get(sigOf(type, text, actions)) ?? null;
}

export async function suggestActions(
  type: string, text: string, actions: CandidateDto[], maxK = 3,
): Promise<SuggestActionsResult> {
  if (actions.length === 0) return { available: true, ranked: [] };
  const sig = sigOf(type, text, actions);
  const hit = _cache.get(sig);
  if (hit) return hit;

  let res: SuggestActionsResult;
  try {
    res = await request<SuggestActionsResult>('/ai/suggest-actions', {
      method: 'POST',
      body: JSON.stringify({ contextType: type, contextText: text, actions, maxK }),
    });
  } catch {
    res = { available: false, ranked: [] }; // сеть/ошибка → фолбэк на правила
  }

  if (res.available) {
    if (_cache.size >= CACHE_MAX) _cache.delete(_cache.keys().next().value!);
    _cache.set(sig, res);
  }
  return res;
}
