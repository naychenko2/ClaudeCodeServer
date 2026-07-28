import { useState, useRef, useEffect, useCallback } from 'react';
import type { ChatItem } from '../types';

// Позиция чтения живёт ровно один reload: пишется только при выгрузке страницы
// (pagehide), в sessionStorage (умирает вместе с вкладкой) и потребляется первым же
// открытием чата. Поэтому переключение чатов и возврат в него позже всегда дают конец
// ленты, а случайный F5 возвращает туда, где читали.
const SCROLL_TTL_MS = 5 * 60 * 1000;

// Разовая уборка бессрочных записей прежнего формата (localStorage) — иначе они
// остались бы у пользователей навсегда.
try {
  for (let i = localStorage.length - 1; i >= 0; i--) {
    const k = localStorage.key(i);
    if (k?.startsWith('cc-scroll-')) localStorage.removeItem(k);
  }
} catch { /* localStorage недоступен */ }

// Скролл-механика ленты чата: прилипание к низу, восстановление позиции чтения
// после перезагрузки страницы, автоскролл в конец при открытии чата, измерение
// высоты плавающего composer и кнопка «вниз».
export function useChatScroll(sessionId: string, items: ChatItem[], isHistoryLoading: boolean, online: boolean) {
  const bottomRef = useRef<HTMLDivElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  // Внутренний контент-блок ленты — именно он растёт при дорендере (картинки base64,
  // syntax highlight, markdown). Наблюдаем за ним, чтобы держать низ после загрузки истории.
  const contentRef = useRef<HTMLDivElement>(null);
  // Плавающий composer переменной высоты — измеряем, чтобы лента упиралась ровно под него
  const composerWrapRef = useRef<HTMLDivElement>(null);
  const [composerH, setComposerH] = useState(96);
  // Прилипание к низу: автоскролл при новых сообщениях только если пользователь уже внизу
  const atBottomRef = useRef(true);
  // Восстановление позиции чтения после reload: храним позицию + высоту ленты per-session.
  const scrollKey = `cc-scroll-${sessionId}`;
  const pendingRestoreRef = useRef<{ top: number; h: number } | null>(null);
  const restoredRef = useRef(false);
  // Показывать плавающую кнопку «вниз», когда пользователь отлистал вверх
  const [showScrollDown, setShowScrollDown] = useState(false);

  // Сброс состояния при смене сессии
  useEffect(() => {
    atBottomRef.current = true;
    restoredRef.current = false;
    setShowScrollDown(false);
    // Загружаем позицию, оставленную выгрузкой страницы (свежую — протухшую игнорируем:
    // sessionStorage переживает bfcache, а через полчаса возврата лента уже неактуальна)
    let saved: { top: number; h: number } | null = null;
    try {
      const raw = sessionStorage.getItem(scrollKey);
      if (raw) {
        const o = JSON.parse(raw);
        if (o && Number.isFinite(o.top) && Number.isFinite(o.h) && Number.isFinite(o.t)
          && Date.now() - o.t < SCROLL_TTL_MS) saved = { top: o.top, h: o.h };
      }
    } catch { /* недоступен sessionStorage / старый формат */ }
    pendingRestoreRef.current = saved;
    if (saved == null) return;
    atBottomRef.current = false;
    // Запись одноразовая: она валидна ровно для первого открытия чата после reload,
    // дальше чат должен открываться в конце ленты. Стираем макротаском, а не сразу —
    // StrictMode в dev перемонтирует эффект синхронно, и второй проход обязан
    // прочитать ту же запись, иначе восстановление не сработает.
    window.setTimeout(() => {
      try { sessionStorage.removeItem(scrollKey); } catch { /* недоступен sessionStorage */ }
    }, 0);
  }, [scrollKey]);

  // Позиция чтения сохраняется только при выгрузке страницы (reload/закрытие вкладки).
  // Уход в другой чат её не пишет — так «вернулся в чат» всегда означает конец ленты.
  useEffect(() => {
    const save = () => {
      const el = scrollRef.current;
      if (!el) return;
      const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
      try {
        if (atBottom) sessionStorage.removeItem(scrollKey);
        else sessionStorage.setItem(scrollKey, JSON.stringify({
          top: Math.round(el.scrollTop), h: Math.round(el.scrollHeight), t: Date.now(),
        }));
      } catch { /* недоступен sessionStorage */ }
    };
    window.addEventListener('pagehide', save);
    return () => window.removeEventListener('pagehide', save);
  }, [scrollKey]);

  // Измеряем высоту плавающего composer → задаём нижний отступ ленты (упор ровно под него)
  useEffect(() => {
    const el = composerWrapRef.current;
    if (!el) return;
    const update = () => setComposerH(el.offsetHeight);
    update();
    const ro = new ResizeObserver(update);
    ro.observe(el);
    return () => ro.disconnect();
  }, [online]);

  // Единая точка проверки позиции скролла
  const syncScrollState = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
    atBottomRef.current = atBottom;
    setShowScrollDown(!atBottom);
  }, []);

  // Следим за изменением высоты scroll-контейнера (resize окна, dock expand)
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const ro = new ResizeObserver(syncScrollState);
    ro.observe(el);
    return () => ro.disconnect();
  }, [syncScrollState]);

  // Прилипание к низу при росте КОНТЕНТА (асинхронный дорендер — картинки, код, markdown)
  useEffect(() => {
    const content = contentRef.current;
    const el = scrollRef.current;
    if (!content || !el) return;
    const ro = new ResizeObserver(() => {
      if (!restoredRef.current && pendingRestoreRef.current != null) {
        // Есть недовосстановленная позиция — держим её
        const pend = pendingRestoreRef.current;
        el.scrollTop = Math.min(pend.top, el.scrollHeight - el.clientHeight);
        if (el.scrollHeight >= pend.h - 50) {
          restoredRef.current = true;
          setShowScrollDown(el.scrollHeight - el.scrollTop - el.clientHeight >= 80);
        }
      } else if (atBottomRef.current) {
        el.scrollTop = el.scrollHeight;
      }
      syncScrollState();
    });
    ro.observe(content);
    return () => ro.disconnect();
  }, [syncScrollState]);

  const handleMessagesScroll = syncScrollState;

  // Программный скролл в конец ленты (клик по плавающей кнопке)
  const scrollToBottom = () => {
    atBottomRef.current = true;
    setShowScrollDown(false);
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  };

  // restoreTick — восстановление позиции при дорендере контента
  const restoreTick = useCallback(() => {
    if (restoredRef.current) return;
    const el = scrollRef.current;
    const pend = pendingRestoreRef.current;
    if (!el) return;
    if (pend == null) { restoredRef.current = true; return; }
    el.scrollTop = Math.min(pend.top, el.scrollHeight - el.clientHeight);
    if (el.scrollHeight >= pend.h - 50) {
      restoredRef.current = true;
      setShowScrollDown(el.scrollHeight - el.scrollTop - el.clientHeight >= 80);
    }
  }, []);

  // Автоскролл при новых сообщениях / восстановлении истории
  useEffect(() => {
    // Если есть сохранённая позиция — восстанавливаем её (не скроллим вниз)
    if (!restoredRef.current && pendingRestoreRef.current != null) {
      const el = scrollRef.current;
      const pend = pendingRestoreRef.current;
      if (el) {
        el.scrollTop = Math.min(pend.top, el.scrollHeight - el.clientHeight);
        if (el.scrollHeight >= pend.h - 50) {
          restoredRef.current = true;
          const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
          setShowScrollDown(!atBottom);
          atBottomRef.current = atBottom;
        } else {
          setShowScrollDown(true);
        }
      }
      return;
    }
    // Нет сохранённой позиции — скроллим в конец
    if (atBottomRef.current) {
      bottomRef.current?.scrollIntoView({ behavior: 'instant' });
      setShowScrollDown(false);
    } else {
      setShowScrollDown(true);
    }
  }, [items, restoreTick]);

  // Финал восстановления после загрузки истории
  useEffect(() => {
    if (isHistoryLoading || restoredRef.current) return;
    if (pendingRestoreRef.current == null) { restoredRef.current = true; return; }
    restoreTick();
    const raf = requestAnimationFrame(restoreTick);
    const done = window.setTimeout(() => { restoredRef.current = true; syncScrollState(); }, 5000);
    return () => { cancelAnimationFrame(raf); clearTimeout(done); };
  }, [isHistoryLoading, restoreTick, syncScrollState]);

  return {
    bottomRef, scrollRef, contentRef, composerWrapRef, composerH,
    showScrollDown, atBottomRef, handleMessagesScroll, scrollToBottom,
  };
}
