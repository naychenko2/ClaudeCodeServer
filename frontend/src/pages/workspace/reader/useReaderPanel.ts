// Состояние панели «Чтение» — один экземпляр на страницу (WorkspacePage/ChatsPage),
// общий между содержимым рельсы, кастомной шапкой и полноэкранным «Развёрнуто».
// Держит стек переходов внутри статьи (ADR-005 §2: тот же URL-периметр, что и у кнопки
// в чате, плюс потолок глубины) и мини-кэш посещённых страниц — «Назад» не бьёт по сети.
//
// Режимы (ADR-006 §2): панель сначала пытается показать страницу ЦЕЛИКОМ в sandbox-iframe
// ('page'); если серверная проба запретила встраивание, страница отдаётся текстом сразу
// (mixed content), или watchdog не дождался отрисовки фрейма — молча переходит в
// MD-чтение ('md') через существующий /read. Вердикт пробы не ветвит поведение дальше
// самого выбора режима: embeddable:false с любым reason и сбой пробы — один путь в MD.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../../../lib/api';
import type { ReaderErrorCode, ReaderPage } from '../../../types';

// Глубина перехода внутри ридера — тот же потолок, что и на сервере (ADR §2/§5)
const MAX_DEPTH = 5;
const BANNER_SEEN_KEY = 'cc_reader_banner_seen';

// ADR-006 §2, watchdog: предел ожидания отрисовки iframe. Браузер не сообщает
// кросс-доменному родителю, что фрейм заблокирован (load срабатывает и на
// заблокированном фрейме, события ошибки нет), поэтому критерий один — страница
// не догрузилась за это время: молча уходим в MD-режим, без видимой ошибки.
const IFRAME_WATCHDOG_MS = 5_000;

export type ReaderMode = 'page' | 'md';

interface Entry {
  url: string;
  mode: ReaderMode;
  // iframe для записи невозможен в принципе: http-страница в https-приложении
  // (mixed content — браузер заблокирует фрейм сам, проба бесполезна; ADR-006 §2).
  // Ручной переключатель в шапке такую запись в режим 'page' не отпускает.
  noIframe: boolean;
  page: ReaderPage | null;
  error: { code: ReaderErrorCode } | null;
}

export interface ReaderPanelState {
  // Открыт ли ридер вообще (панель могла быть открыта из рельсы без выбранной ссылки)
  open: boolean;
  loading: boolean;
  url: string | null;
  // Текущий режим верхней записи стека; null — запись нет. Пока loading (проба или
  // MD-загрузка) режим шапкой не показывается — он ещё не определён (макет §2.3)
  mode: ReaderMode | null;
  // Страница принципиально не может быть показана в iframe (mixed content) —
  // ручной переключатель режимов для такой записи выключен
  iframeUnavailable: boolean;
  page: ReaderPage | null;
  error: { code: ReaderErrorCode } | null;
  canGoBack: boolean;
  expanded: boolean;
  bannerDismissed: boolean;
}

export interface ReaderPanelActions {
  // Новое чтение — из клика по ссылке в ленте или кнопки-компаньона (сбрасывает стек)
  openUrl: (url: string) => void;
  // Переход по ссылке ВНУТРИ статьи — наращивает стек «Назад» (всегда MD-режим:
  // навигация внутри iframe остаётся во фрейме, стек панели на неё не реагирует)
  follow: (url: string) => void;
  back: () => void;
  retry: () => void;
  openInBrowser: () => void;
  toggleExpand: () => void;
  dismissBanner: () => void;
  // Ручное переключение режимов из шапки (ADR-006 §2): серверный вердикт авторитетен
  // для заголовков, но не всеточен — человек всегда может сменить режим в обе стороны
  toggleMode: () => void;
  // Iframe догрузился (событие load) — watchdog снимается, отрисовка состоялась
  onIframeLoad: () => void;
  // Сброс к пустому состоянию (кнопка ✕ в шапке ридера — своей, а не системной панельной:
  // ✕ на панели с прочитанным гасит именно ЧТЕНИЕ, а не выселяет саму панель из рельсы)
  closeReader: () => void;
}

function readBannerSeen(): boolean {
  try { return localStorage.getItem(BANNER_SEEN_KEY) === '1'; } catch { return false; }
}

// Mixed content: http-страница при приложении на https (ADR-006 §2) — браузер такой
// iframe заблокирует сам, серверная проба бесполезна, отдаём текст сразу
function isMixedContent(url: string): boolean {
  if (typeof window === 'undefined' || window.location.protocol !== 'https:') return false;
  try { return new URL(url).protocol === 'http:'; } catch { return false; }
}

export function useReaderPanel(): { state: ReaderPanelState; actions: ReaderPanelActions } {
  const [stack, setStack] = useState<Entry[]>([]);
  const [loading, setLoading] = useState(false);
  const [expanded, setExpanded] = useState(false);
  const [bannerDismissed, setBannerDismissed] = useState(readBannerSeen);
  // Счётчик запроса — устаревший ответ (например, после «Назад» или второго клика по
  // другой ссылке) молча игнорируется, а не перезаписывает уже показанную страницу
  const reqIdRef = useRef(0);
  const watchdogRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Актуальный стек для отложенных колбэков (watchdog) — снимок в замыкании устаревает
  const stackRef = useRef(stack);
  useEffect(() => { stackRef.current = stack; }, [stack]);

  const current = stack[stack.length - 1] ?? null;

  const disarmWatchdog = useCallback(() => {
    if (watchdogRef.current != null) {
      clearTimeout(watchdogRef.current);
      watchdogRef.current = null;
    }
  }, []);

  // Размонтирование страницы гасит взведённый watchdog
  useEffect(() => disarmWatchdog, [disarmWatchdog]);

  // MD-загрузка страницы (существующий путь ADR-005, без изменений): заменяет верхнюю
  // запись стека результатом /read. mode всегда 'md' — это и есть текстовый режим.
  const load = useCallback((url: string, nextStack: Entry[]) => {
    const myReq = ++reqIdRef.current;
    disarmWatchdog();
    setStack(nextStack);
    setLoading(true);
    api.reader.read(url).then(res => {
      if (reqIdRef.current !== myReq) return;
      setLoading(false);
      setStack(prev => {
        const idx = prev.length - 1;
        if (idx < 0 || prev[idx].url !== url) return prev;
        const prevEntry = prev[idx];
        const entry: Entry = res.ok
          ? { url, mode: 'md', noIframe: prevEntry.noIframe, page: res.page, error: null }
          : { url, mode: 'md', noIframe: prevEntry.noIframe, page: null, error: res.error };
        return [...prev.slice(0, idx), entry];
      });
    });
  }, [disarmWatchdog]);

  // Взвести watchdog: если iframe не догрузился за IFRAME_WATCHDOG_MS — молча
  // переключить верхнюю запись в MD и запустить /read (ADR-006 §2)
  const armWatchdog = useCallback((myReq: number, url: string) => {
    disarmWatchdog();
    watchdogRef.current = setTimeout(() => {
      watchdogRef.current = null;
      // К моменту срабатывания пользователь мог уйти (другая ссылка, «Закрыть») —
      // проверяем и счётчик запроса, и что верх стека всё ещё эта страница
      if (reqIdRef.current !== myReq) return;
      const top = stackRef.current[stackRef.current.length - 1];
      if (!top || top.url !== url || top.mode !== 'page') return;
      load(url, stackRef.current);
    }, IFRAME_WATCHDOG_MS);
  }, [disarmWatchdog, load]);

  const openUrl = useCallback((url: string) => {
    const myReq = ++reqIdRef.current;
    disarmWatchdog();
    const noIframe = isMixedContent(url);
    const entry: Entry = { url, mode: 'md', noIframe, page: null, error: null };
    setStack([entry]);
    setLoading(true);
    setExpanded(false);
    // Оговорённый ADR-006 §2 случай: mixed content — страница отдаётся текстом сразу
    if (noIframe) { load(url, [entry]); return; }
    api.reader.embedCheck(url).then(res => {
      if (reqIdRef.current !== myReq) return;
      if (res.embeddable) {
        // Разрешающий вердикт — sandbox-iframe под watchdog'ом
        setStack([{ ...entry, mode: 'page' }]);
        setLoading(false);
        armWatchdog(myReq, url);
      } else {
        // Запрещающий вердикт (любой reason) или сбой пробы — существующий текстовый путь
        load(url, [entry]);
      }
    });
  }, [disarmWatchdog, armWatchdog, load]);

  const follow = useCallback((url: string) => {
    if (stack.length >= MAX_DEPTH) return; // потолок глубины (ADR §2/§5) — молча не растим стек дальше
    load(url, [...stack, { url, mode: 'md', noIframe: false, page: null, error: null }]);
  }, [stack, load]);

  const back = useCallback(() => {
    disarmWatchdog(); // снятая со стека запись могла быть iframe под watchdog'ом
    setStack(prev => (prev.length > 1 ? prev.slice(0, -1) : prev));
  }, [disarmWatchdog]);

  const retry = useCallback(() => {
    if (!current) return;
    load(current.url, stack);
  }, [current, load, stack]);

  const openInBrowser = useCallback(() => {
    if (current) window.open(current.url, '_blank', 'noopener,noreferrer');
  }, [current]);

  const toggleExpand = useCallback(() => setExpanded(v => !v), []);

  const toggleMode = useCallback(() => {
    const top = stackRef.current[stackRef.current.length - 1];
    if (!top || loading || top.error) return;
    if (top.mode === 'page') {
      // В MD: если статья уже была прочитана (например, фолбэк случался раньше) —
      // мгновенно и без сети, иначе — обычный /read
      if (top.page) {
        disarmWatchdog();
        setStack(prev => {
          const idx = prev.length - 1;
          if (idx < 0 || prev[idx].url !== top.url) return prev;
          return [...prev.slice(0, idx), { ...prev[idx], mode: 'md' as ReaderMode }];
        });
      } else {
        load(top.url, stackRef.current);
      }
      return;
    }
    // В iframe: mixed content не отпускаем (браузер фрейм всё равно заблокирует)
    if (top.noIframe) return;
    setStack(prev => {
      const idx = prev.length - 1;
      if (idx < 0 || prev[idx].url !== top.url) return prev;
      return [...prev.slice(0, idx), { ...prev[idx], mode: 'page' as ReaderMode }];
    });
    setLoading(false);
    armWatchdog(reqIdRef.current, top.url);
  }, [loading, disarmWatchdog, load, armWatchdog]);

  const onIframeLoad = useCallback(() => {
    // Отрисовка состоялась (или браузер показал свой экран отказа — его человек
    // видит и может вернуться в MD ручным переключателем): watchdog больше не нужен
    disarmWatchdog();
  }, [disarmWatchdog]);

  const closeReader = useCallback(() => {
    reqIdRef.current++; // гасим уже летящий запрос, если был
    disarmWatchdog();
    setStack([]);
    setLoading(false);
    setExpanded(false);
  }, [disarmWatchdog]);

  const dismissBanner = useCallback(() => {
    setBannerDismissed(true);
    try { localStorage.setItem(BANNER_SEEN_KEY, '1'); } catch { /* приватный режим — обойдёмся без запоминания */ }
  }, []);

  const state = useMemo<ReaderPanelState>(() => ({
    open: stack.length > 0,
    loading,
    url: current?.url ?? null,
    mode: current?.mode ?? null,
    iframeUnavailable: current?.noIframe ?? false,
    page: current?.page ?? null,
    error: current?.error ?? null,
    canGoBack: stack.length > 1,
    expanded,
    bannerDismissed,
  }), [stack, loading, current, expanded, bannerDismissed]);

  const actions = useMemo<ReaderPanelActions>(() => ({
    openUrl, follow, back, retry, openInBrowser, toggleExpand, dismissBanner, toggleMode, onIframeLoad, closeReader,
  }), [openUrl, follow, back, retry, openInBrowser, toggleExpand, dismissBanner, toggleMode, onIframeLoad, closeReader]);

  return { state, actions };
}
