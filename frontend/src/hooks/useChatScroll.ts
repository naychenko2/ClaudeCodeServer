import { useState, useRef, useEffect, useCallback } from 'react';
import type { ChatItem } from '../types';

// Скролл-механика ленты чата: прилипание к низу, восстановление позиции чтения
// после refresh, измерение высоты плавающего composer и кнопка «вниз».
// Механически вынесено из ChatPanel — поведение без изменений.
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
  // Высота нужна, чтобы дождаться асинхронного дорендера (картинки base64 грузятся сетевыми
  // запросами, дольше любого фиксированного таймаута): держим позицию, пока лента не дорастёт.
  const scrollKey = `cc-scroll-${sessionId}`;
  const pendingRestoreRef = useRef<{ top: number; h: number } | null>(null); // к восстановлению (null = нечего/был внизу)
  const restoredRef = useRef(false);                                          // восстановление для текущей сессии выполнено
  // Показывать плавающую кнопку «вниз», когда пользователь отлистал вверх
  const [showScrollDown, setShowScrollDown] = useState(false);

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

  // Единая точка проверки позиции скролла — вызывается из onScroll, ResizeObserver и эффектов
  const syncScrollState = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
    atBottomRef.current = atBottom;
    setShowScrollDown(!atBottom);
    // После восстановления — запоминаем позицию чтения + высоту ленты, чтобы пережить refresh.
    // Внизу позицию не храним: по умолчанию прилипаем к низу.
    if (restoredRef.current) {
      try {
        if (atBottom) localStorage.removeItem(scrollKey);
        else localStorage.setItem(scrollKey, JSON.stringify({ top: Math.round(el.scrollTop), h: Math.round(el.scrollHeight) }));
      } catch { /* localStorage недоступен — не критично */ }
    }
  }, [scrollKey]);

  // Один тик восстановления: ставим сохранённую позицию и финализируем, когда лента доросла
  // до сохранённой высоты (значит весь асинхронный контент дорендерился — позиция точна).
  const restoreTick = useCallback(() => {
    if (restoredRef.current) return;
    const el = scrollRef.current;
    const pend = pendingRestoreRef.current;
    if (!el) return;
    if (pend == null) { restoredRef.current = true; return; }
    el.scrollTop = Math.min(pend.top, el.scrollHeight - el.clientHeight);
    // Лента дорендерилась до сохранённой высоты (с допуском) — позиция стабильна, выходим в обычный режим
    if (el.scrollHeight >= pend.h - 50) {
      restoredRef.current = true;
      setShowScrollDown(el.scrollHeight - el.scrollTop - el.clientHeight >= 80);
    }
  }, []);

  // При смене сессии — поднимаем сохранённую позицию чтения для восстановления после refresh
  useEffect(() => {
    restoredRef.current = false;
    let saved: { top: number; h: number } | null = null;
    try {
      const raw = localStorage.getItem(scrollKey);
      if (raw) {
        const o = JSON.parse(raw);
        if (o && Number.isFinite(o.top) && Number.isFinite(o.h)) saved = { top: o.top, h: o.h };
      }
    } catch { /* нет доступа к localStorage / старый формат */ }
    pendingRestoreRef.current = saved;
    if (saved != null) atBottomRef.current = false; // есть что восстановить — не прыгаем вниз
  }, [scrollKey]);

  // Следим за изменением высоты scroll-контейнера (resize окна, dock expand) — переразмер
  // может сделать позицию «не внизу» без события scroll
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const ro = new ResizeObserver(syncScrollState);
    ro.observe(el);
    return () => ro.disconnect();
  }, [syncScrollState]);

  // Прилипание к низу при росте КОНТЕНТА (не контейнера): история после refresh
  // дорендеривается асинхронно (картинки base64, syntax highlight, markdown) — высота
  // растёт уже после первичного scrollIntoView и низ «уезжает». Пока пользователь у низа,
  // доскролливаем в конец на каждый прирост высоты. Отлистнул вверх → atBottom=false → не дёргаем.
  useEffect(() => {
    const content = contentRef.current;
    const el = scrollRef.current;
    if (!content || !el) return;
    const ro = new ResizeObserver(() => {
      if (!restoredRef.current && pendingRestoreRef.current != null) {
        // история ещё дорендеривается — держим сохранённую позицию, пока лента не дорастёт
        restoreTick();
      } else if (atBottomRef.current) {
        el.scrollTop = el.scrollHeight;
      }
      syncScrollState();
    });
    ro.observe(content);
    return () => ro.disconnect();
  }, [syncScrollState, restoreTick]);

  const handleMessagesScroll = syncScrollState;

  // Программный скролл в конец ленты (клик по плавающей кнопке)
  const scrollToBottom = () => {
    atBottomRef.current = true;
    setShowScrollDown(false);
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  };

  useEffect(() => {
    // Пока восстанавливаем позицию после refresh — низ не трогаем, держим сохранённую точку
    if (!restoredRef.current && pendingRestoreRef.current != null) {
      restoreTick();
      setShowScrollDown(true);
      return;
    }
    // Прокручиваем вниз только если пользователь у нижней точки (не отрываем его от истории)
    if (atBottomRef.current) {
      bottomRef.current?.scrollIntoView({ behavior: 'instant' });
      setShowScrollDown(false);
    } else {
      // пришёл новый контент, а пользователь читает выше — подсветим кнопку «вниз»
      setShowScrollDown(true);
    }
  }, [items, restoreTick]);

  // Восстановление позиции после загрузки истории. Финал привязан не к таймеру, а к достижению
  // сохранённой высоты ленты (restoreTick) — ждём, пока картинки base64 догрузятся по сети.
  // Таймер — лишь страховка на случай, если лента так и не дорастёт (контент стал короче).
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
