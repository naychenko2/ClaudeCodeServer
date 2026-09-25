// Котировка цены до запуска (ADR-017, раздел 4): пересчёт с дебаунсом при смене
// поставщика, модели, числа вариантов и признаков запроса. Ничего не тратит.

import { useEffect, useState } from 'react';
import type { ImageEditorApi, ImageEditQuote, ImageEditQuoteRequest } from '../../api/imageEditor';

// Higgsfield считает цену препроверкой с загрузкой входов — ей дебаунс длиннее
const debounceFor = (provider: string) => (provider === 'higgsfield' ? 800 : 300);

export function useQuote(api: ImageEditorApi, projectId: string, req: ImageEditQuoteRequest | null) {
  const [state, setState] = useState<{ key: string; quote: ImageEditQuote | null; error: string | null }>(
    { key: '', quote: null, error: null });
  const key = req ? JSON.stringify(req) : '';

  useEffect(() => {
    if (!req) return;
    let alive = true;
    const t = setTimeout(() => {
      api.quote(projectId, req)
        .then(quote => { if (alive) setState({ key, quote, error: null }); })
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
  };
}
