// Состояние панели «Чтение» — один экземпляр на страницу (WorkspacePage/ChatsPage),
// общий между содержимым рельсы, кастомной шапкой и полноэкранным «Развёрнуто».
// Держит стек переходов внутри статьи (ADR-005 §2: тот же URL-периметр, что и у кнопки
// в чате, плюс потолок глубины) и мини-кэш посещённых страниц — «Назад» не бьёт по сети.
import { useCallback, useMemo, useRef, useState } from 'react';
import { api } from '../../../lib/api';
import type { ReaderErrorCode, ReaderPage } from '../../../types';

// Глубина перехода внутри ридера — тот же потолок, что и на сервере (ADR §2/§5)
const MAX_DEPTH = 5;
const BANNER_SEEN_KEY = 'cc_reader_banner_seen';

interface Entry {
  url: string;
  page: ReaderPage | null;
  error: { code: ReaderErrorCode } | null;
}

export interface ReaderPanelState {
  // Открыт ли ридер вообще (панель могла быть открыта из рельсы без выбранной ссылки)
  open: boolean;
  loading: boolean;
  url: string | null;
  page: ReaderPage | null;
  error: { code: ReaderErrorCode } | null;
  canGoBack: boolean;
  expanded: boolean;
  bannerDismissed: boolean;
}

export interface ReaderPanelActions {
  // Новое чтение — из кнопки-компаньона в чате (сбрасывает стек переходов)
  openUrl: (url: string) => void;
  // Переход по ссылке ВНУТРИ статьи — наращивает стек «Назад»
  follow: (url: string) => void;
  back: () => void;
  retry: () => void;
  openInBrowser: () => void;
  toggleExpand: () => void;
  dismissBanner: () => void;
  // Сброс к пустому состоянию (кнопка ✕ в шапке ридера — своей, а не системной панельной:
  // ✕ на панели с прочитанным гасит именно ЧТЕНИЕ, а не выселяет саму панель из рельсы)
  closeReader: () => void;
}

function readBannerSeen(): boolean {
  try { return localStorage.getItem(BANNER_SEEN_KEY) === '1'; } catch { return false; }
}

export function useReaderPanel(): { state: ReaderPanelState; actions: ReaderPanelActions } {
  const [stack, setStack] = useState<Entry[]>([]);
  const [loading, setLoading] = useState(false);
  const [expanded, setExpanded] = useState(false);
  const [bannerDismissed, setBannerDismissed] = useState(readBannerSeen);
  // Счётчик запроса — устаревший ответ (например, после «Назад» или второго клика по
  // другой ссылке) молча игнорируется, а не перезаписывает уже показанную страницу
  const reqIdRef = useRef(0);

  const current = stack[stack.length - 1] ?? null;

  const load = useCallback((url: string, nextStack: Entry[]) => {
    const myReq = ++reqIdRef.current;
    setStack(nextStack);
    setLoading(true);
    api.reader.read(url).then(res => {
      if (reqIdRef.current !== myReq) return;
      setLoading(false);
      setStack(prev => {
        const idx = prev.length - 1;
        if (idx < 0 || prev[idx].url !== url) return prev;
        const entry: Entry = res.ok ? { url, page: res.page, error: null } : { url, page: null, error: res.error };
        return [...prev.slice(0, idx), entry];
      });
    });
  }, []);

  const openUrl = useCallback((url: string) => {
    load(url, [{ url, page: null, error: null }]);
    setExpanded(false);
  }, [load]);

  const follow = useCallback((url: string) => {
    if (stack.length >= MAX_DEPTH) return; // потолок глубины (ADR §2/§5) — молча не растим стек дальше
    load(url, [...stack, { url, page: null, error: null }]);
  }, [stack, load]);

  const back = useCallback(() => {
    setStack(prev => (prev.length > 1 ? prev.slice(0, -1) : prev));
  }, []);

  const retry = useCallback(() => {
    if (!current) return;
    load(current.url, stack);
  }, [current, load, stack]);

  const openInBrowser = useCallback(() => {
    if (current) window.open(current.url, '_blank', 'noopener,noreferrer');
  }, [current]);

  const toggleExpand = useCallback(() => setExpanded(v => !v), []);

  const closeReader = useCallback(() => {
    reqIdRef.current++; // гасим уже летящий запрос, если был
    setStack([]);
    setLoading(false);
    setExpanded(false);
  }, []);

  const dismissBanner = useCallback(() => {
    setBannerDismissed(true);
    try { localStorage.setItem(BANNER_SEEN_KEY, '1'); } catch { /* приватный режим — обойдёмся без запоминания */ }
  }, []);

  const state = useMemo<ReaderPanelState>(() => ({
    open: stack.length > 0,
    loading,
    url: current?.url ?? null,
    page: current?.page ?? null,
    error: current?.error ?? null,
    canGoBack: stack.length > 1,
    expanded,
    bannerDismissed,
  }), [stack, loading, current, expanded, bannerDismissed]);

  const actions = useMemo<ReaderPanelActions>(() => ({
    openUrl, follow, back, retry, openInBrowser, toggleExpand, dismissBanner, closeReader,
  }), [openUrl, follow, back, retry, openInBrowser, toggleExpand, dismissBanner, closeReader]);

  return { state, actions };
}
