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

export function useQuote(api: ImageEditorApi, projectId: string, req: ImageEditQuoteRequest | null) {
  const [state, setState] = useState<{ key: string; quote: ImageEditQuote | null; error: string | null }>(
    { key: '', quote: null, error: null });
  const [last, setLast] = useState<{ req: ImageEditQuoteRequest; quote: ImageEditQuote } | null>(null);
  const key = req ? JSON.stringify(req) : '';

  useEffect(() => {
    if (!req) return;
    let alive = true;
    const t = setTimeout(() => {
      api.quote(projectId, req)
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
