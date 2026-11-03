// «Вернуть мою» (F3b, ADR-038): возвращает формулировку человека на место той, которой её
// заменил ИИ. Тело запроса — пара из overrides[] страницы: statement → current, previous →
// restore; без previous («Истории» не нашли строку) вызов не уходит.
//
// Конкурентность: двойной клик — один запрос (замок в ref, state не успевает); ответ,
// пришедший после смены страницы, отбрасывается по ключу «раздел + путь» — ни состояния, ни
// перечитки, ни результата для вызывающего. 409 wiki_page_changed ничего не перезаписывает:
// страница перечитывается, человек видит свежую версию и жмёт ещё раз. Отказ любого вида ловится
// здесь — rejection наружу не выходит, текст уходит в error для Notice.

import { useCallback, useEffect, useRef, useState } from 'react';
import { WikiError, loadWikiPage, restoreWikiPage, type WikiOverrideDto } from 'aihome_shell/kit';
import { OVERRIDE_TEXT, wikiErrorText } from './wikiLabels';

export type RestoreOutcome = 'done' | 'unchanged' | 'conflict' | 'failed' | 'busy' | 'stale' | 'nopair';

interface UiState { id: string; busy: boolean; error: string | null }

export function useRestoreWikiStatement(sectionKey: string, pageKey: string) {
  const pageId = `${sectionKey}\n${pageKey}`;
  // Текущая страница хука — для сверки после await; пишется в эффекте, а не во время рендера
  const idRef = useRef<string | null>(pageId);
  useEffect(() => { idRef.current = pageId; }, [pageId]);
  useEffect(() => () => { idRef.current = null; }, []);
  // Страница, по которой запрос в полёте: на другой странице новый запрос не блокируется
  const inFlightRef = useRef<string | null>(null);
  const [ui, setUi] = useState<UiState>({ id: pageId, busy: false, error: null });
  // Состояние чужой страницы не показываем: оно сбрасывается самим ключом
  const view = ui.id === pageId ? ui : { id: pageId, busy: false, error: null };

  const restore = useCallback(async (o: WikiOverrideDto, baseHash: string | null): Promise<RestoreOutcome> => {
    if (o.previous === null || o.statement === null) return 'nopair';
    if (inFlightRef.current === pageId) return 'busy';
    inFlightRef.current = pageId;
    setUi({ id: pageId, busy: true, error: null });
    const fail = (error: string, outcome: RestoreOutcome): RestoreOutcome => {
      setUi({ id: pageId, busy: false, error });
      return outcome;
    };
    try {
      const r = await restoreWikiPage(sectionKey, pageKey, baseHash, o.statement, o.previous);
      if (idRef.current !== pageId) return 'stale';
      await loadWikiPage(sectionKey, pageKey).catch(() => undefined);
      if (idRef.current !== pageId) return 'stale';
      setUi({ id: pageId, busy: false, error: null });
      return r.status === 'applied' ? 'done' : 'unchanged';
    } catch (e) {
      if (idRef.current !== pageId) return 'stale';
      if (e instanceof WikiError && e.code === 'wiki_page_changed') {
        await loadWikiPage(sectionKey, pageKey).catch(() => undefined);
        if (idRef.current !== pageId) return 'stale';
        return fail(OVERRIDE_TEXT.changed, 'conflict');
      }
      const text = e instanceof WikiError
        ? (e.code === 'wiki_edit_invalid' && e.message ? e.message : wikiErrorText(e))
        : OVERRIDE_TEXT.failed;
      return fail(text, 'failed');
    } finally {
      if (inFlightRef.current === pageId) inFlightRef.current = null;
      // Запрос ушедшей страницы не оставляет у неё вечного «занято»
      setUi(st => (st.id === pageId && st.busy ? { ...st, busy: false } : st));
    }
  }, [pageId, sectionKey, pageKey]);

  const clearError = useCallback(() => setUi(s => (s.id === pageId ? { ...s, error: null } : s)), [pageId]);

  return { busy: view.busy, error: view.error, restore, clearError };
}
