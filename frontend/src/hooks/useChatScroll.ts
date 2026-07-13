import { useState, useRef, useEffect, useCallback } from 'react';
import type { ChatItem } from '../types';

// Скролл-механика ленты чата: прилипание к низу, восстановление сохранённой
// позиции чтения после refresh (если пользователь отлистал вверх), автоскролл
// в конец при открытии чата без сохранённой позиции, измерение высоты
// плавающего composer и кнопка «вниз».
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
  // Восстановление позиции чтения после refresh: храним позицию + высоту ленты per-session.
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
    // Загружаем сохранённую позицию
    let saved: { top: number; h: number } | null = null;
    try {
      const raw = localStorage.getItem(scrollKey);
      if (raw) {
        const o = JSON.parse(raw);
        if (o && Number.isFinite(o.top) && Number.isFinite(o.h)) saved = { top: o.top, h: o.h };
      }
    } catch { /* недоступен localStorage / старый формат */ }
    pendingRestoreRef.current = saved;
    if (saved != null) atBottomRef.current = false;
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
    // Сохраняем позицию (кроме случая «внизу»)
    if (restoredRef.current) {
      try {
        if (atBottom) localStorage.removeItem(scrollKey);
        else localStorage.setItem(scrollKey, JSON.stringify({ top: Math.round(el.scrollTop), h: Math.round(el.scrollHeight) }));
      } catch { /* localStorage недоступен */ }
    }
  }, [scrollKey]);

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
