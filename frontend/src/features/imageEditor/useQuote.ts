// Котировка цены до запуска (ADR-017, раздел 4): пересчёт с дебаунсом при смене
// поставщика, модели, числа вариантов и признаков запроса. Ничего не тратит.

import { useEffect, useState } from 'react';
import type { ImageEditEstimate, ImageEditorApi, ImageEditQuote, ImageEditQuoteRequest } from './api';

// Пока новая котировка едет, цена той же модели берётся из прошлой, пересчитанной на число
// вариантов: иначе у модели без ориентира в каталоге («Авто») низ мигал «Цена уточняется»
// при каждом образце, роли или пометке — у Higgsfield препроверка идёт секунды
export function staleEstimate(
  last: { req: ImageEditQuoteRequest; quote: ImageEditQuote } | null, req: ImageEditQuoteRequest | null,
): ImageEditEstimate | null {
  if (!last || !req || last.req.provider !== req.provider || last.req.model !== req.model) return null;
  const e = last.quote.estimate;
  if (e.amount == null) return null;
  return { ...e, amount: Math.round((e.amount / Math.max(1, last.req.count)) * req.count * 10000) / 10000, approx: true };
}

// Higgsfield считает цену препроверкой с загрузкой входов — ей дебаунс длиннее
const debounceFor = (provider: string) => (provider === 'higgsfield' ? 800 : 300);

// Одну котировку держат сразу несколько хозяев (полоса, панель, поле ввода, карточка ленты):
// одинаковый запрос уходит на сервер один раз, остальные ждут тот же ответ. Готовый ответ
// живёт ещё QUOTE_SHARE_MS — на разбег таймеров дебаунса у смонтированных хозяев
const QUOTE_SHARE_MS = 2000;
const _shared = new Map<string, { p: Promise<ImageEditQuote>; settled: number | null }>();

export function sharedQuote(api: ImageEditorApi, projectId: string, req: ImageEditQuoteRequest): Promise<ImageEditQuote> {
  const now = Date.now();
  for (const [k, v] of _shared) if (v.settled !== null && now - v.settled >= QUOTE_SHARE_MS) _shared.delete(k);
  const key = `${projectId}\n${JSON.stringify(req)}`;
  const hit = _shared.get(key);
  if (hit) return hit.p;
  const entry: { p: Promise<ImageEditQuote>; settled: number | null } = { p: api.quote(projectId, req), settled: null };
  _shared.set(key, entry);
  // Ошибку не делим дальше её хозяев: следующий запрос пойдёт заново
  entry.p.then(() => { entry.settled = Date.now(); }, () => { _shared.delete(key); });
  return entry.p;
}

export function __resetSharedQuotes() {
  _shared.clear();
}

export function useQuote(api: ImageEditorApi, projectId: string, req: ImageEditQuoteRequest | null) {
  const [state, setState] = useState<{ key: string; quote: ImageEditQuote | null; error: string | null }>(
    { key: '', quote: null, error: null });
  const [last, setLast] = useState<{ req: ImageEditQuoteRequest; quote: ImageEditQuote } | null>(null);
  const key = req ? JSON.stringify(req) : '';

  useEffect(() => {
    if (!req) return;
    let alive = true;
    const t = setTimeout(() => {
      sharedQuote(api, projectId, req)
        .then(quote => { if (alive) { setState({ key, quote, error: null }); setLast({ req, quote }); } })
        .catch((e: Error) => { if (alive) setState({ key, quote: null, error: e.message }); });
    }, debounceFor(req.provider));
    return () => { alive = false; clearTimeout(t); };
    // req сравнивается по ключу: объект пересоздаётся каждый рендер
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, projectId, key]);

  const fresh = state.key === key;
  return {
    quote: fresh ? state.quote : null,
    error: fresh ? state.error : null,
    loading: !!req && !fresh,
    stale: fresh ? null : staleEstimate(last, req),
  };
}
