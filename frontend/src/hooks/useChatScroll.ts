import { useState, useRef, useEffect, useLayoutEffect, useCallback } from 'react';
import type { ChatItem } from '../types';

// Позиция чтения живёт ровно один reload: пишется только при выгрузке страницы
// (pagehide), в sessionStorage (умирает вместе с вкладкой) и потребляется первым же
// открытием чата. Поэтому переключение чатов и возврат в него позже всегда дают конец
// ленты, а случайный F5 возвращает туда, где читали.
const SCROLL_TTL_MS = 5 * 60 * 1000;
// Порог «лента у низа»: запас на дробные пиксели и хвостовые отступы
const BOTTOM_EPS = 80;

// Разовая уборка бессрочных записей прежнего формата (localStorage) — иначе они
// остались бы у пользователей навсегда.
try {
  for (let i = localStorage.length - 1; i >= 0; i--) {
    const k = localStorage.key(i);
    if (k?.startsWith('cc-scroll-')) localStorage.removeItem(k);
  }
} catch { /* localStorage недоступен */ }

// Последняя измеренная высота композера — стартовое значение для следующего чата.
// Иначе лента каждый раз открывается с запасом в 96px, а через кадр композер домеряет
// свои ~180 и область прокрутки ужимается: открытие чата даёт рывок снизу.
let _lastComposerH = 96;

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
  const [composerH, setComposerH] = useState(_lastComposerH);
  // Прилипание к низу: автоскролл при новых сообщениях, пока лента «приклеена» к концу.
  // ГЛАВНЫЙ ИНВАРИАНТ: отклеивает ленту ТОЛЬКО жест пользователя (колесо, тач, клавиши,
  // перетаскивание полосы). Программные сдвиги геометрии — composer домерил свою высоту
  // (96 → 181px, лента ужалась) или лента дорендерилась (подсветка кода, картинки, mermaid) —
  // прилипание не снимают. Раньше снимали: первый же такой сдвиг ронял флаг в false, и весь
  // дальнейший дорендер (в тяжёлом чате — десятки тысяч px) шёл мимо, а лента замирала там,
  // где её застал первый кадр, то есть у начала.
  const atBottomRef = useRef(true);
  const userGestureRef = useRef(false);
  // Восстановление позиции чтения после reload: храним позицию + высоту ленты per-session.
  const scrollKey = `cc-scroll-${sessionId}`;
  const pendingRestoreRef = useRef<{ top: number; h: number } | null>(null);
  const restoredRef = useRef(false);
  // Показывать плавающую кнопку «вниз», когда пользователь отлистал вверх
  const [showScrollDown, setShowScrollDown] = useState(false);
  // Лента сдвинута от начала — шапка приподнимается тенью над уехавшим под неё текстом.
  // Порог в пару пикселей, а не строгий ноль: инерционная прокрутка на тач-устройствах
  // любит замирать чуть ниже начала, и тень мигала бы на каждом касании
  const [scrolled, setScrolled] = useState(false);

  // Сброс состояния при смене сессии. Layout-эффект: скролл выставляется до отрисовки
  // кадра, поэтому лента не успевает мигнуть началом.
  useLayoutEffect(() => {
    atBottomRef.current = true;
    restoredRef.current = false;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс и восстановление позиции скролла при смене сессии
    setShowScrollDown(false);
    // eslint-disable-next-line react-hooks/set-state-in-effect -- иначе тень шапки переезжает из прошлого чата в новый
    setScrolled(false);
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
    // Конец ленты — состояние по умолчанию в любом случае: и когда сохранённой позиции нет,
    // и пока лента не доросла до сохранённой высоты. Полагаться на эффект автоскролла по
    // items нельзя — вернулись в уже загруженный чат, массив тот же по ссылке, эффект не
    // перезапустится, и лента осталась бы там, где её отлистали.
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
    if (saved == null) return;
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
      const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_EPS;
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
    const update = () => { _lastComposerH = el.offsetHeight; setComposerH(el.offsetHeight); };
    update();
    const ro = new ResizeObserver(update);
    ro.observe(el);
    return () => ro.disconnect();
  }, [online]);

  // Единая точка проверки позиции скролла
  const syncScrollState = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_EPS;
    // Отклеиваем от низа только по живому жесту; приклеиваем обратно сразу, как пользователь
    // сам довёл ленту до конца.
    if (atBottom || userGestureRef.current) atBottomRef.current = atBottom;
    setShowScrollDown(!atBottom);
    setScrolled(el.scrollTop > 2);
  }, []);

  // Жесты пользователя: пока идёт жест (и 400мс после) сдвиг позиции считаем его волей —
  // только такой сдвиг снимает прилипание к низу.
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    let timer = 0;
    const mark = () => {
      userGestureRef.current = true;
      window.clearTimeout(timer);
      timer = window.setTimeout(() => { userGestureRef.current = false; }, 400);
    };
    // Клавиатурная прокрутка идёт мимо ленты (фокуса у неё нет) — слушаем окно, но только
    // навигационные клавиши и не во время набора текста в композере.
    const NAV = ['PageUp', 'PageDown', 'Home', 'End', 'ArrowUp', 'ArrowDown', ' '];
    const onKey = (e: KeyboardEvent) => {
      if (!NAV.includes(e.key)) return;
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      mark();
    };
    el.addEventListener('wheel', mark, { passive: true });
    el.addEventListener('touchmove', mark, { passive: true });
    el.addEventListener('mousedown', mark); // перетаскивание полосы прокрутки
    window.addEventListener('keydown', onKey);
    return () => {
      window.clearTimeout(timer);
      el.removeEventListener('wheel', mark);
      el.removeEventListener('touchmove', mark);
      el.removeEventListener('mousedown', mark);
      window.removeEventListener('keydown', onKey);
    };
  }, []);

  // Следим за изменением высоты scroll-контейнера (composer домерился, resize окна,
  // dock expand): область прокрутки ужимается — конец ленты уезжает, и его надо догнать.
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => {
      if (atBottomRef.current && pendingRestoreRef.current == null) el.scrollTop = el.scrollHeight;
      syncScrollState();
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [syncScrollState]);

  // Применяем сохранённую позицию, когда лента доросла до сохранённой высоты. Пока не
  // доросла — держим конец ленты: раньше здесь стоял min(top, текущая высота), то есть
  // при неудачном восстановлении (лента не дорастает до записанной высоты) чат так и
  // оставался у начала.
  const applyRestore = useCallback(() => {
    const el = scrollRef.current;
    const pend = pendingRestoreRef.current;
    if (!el || pend == null) return;
    if (el.scrollHeight < pend.h - 50) { el.scrollTop = el.scrollHeight; return; }
    el.scrollTop = Math.min(pend.top, el.scrollHeight - el.clientHeight);
    restoredRef.current = true;
    pendingRestoreRef.current = null;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_EPS;
    atBottomRef.current = atBottom;
    setShowScrollDown(!atBottom);
  }, []);

  // Прилипание к низу при росте КОНТЕНТА (асинхронный дорендер — картинки, код, markdown)
  useEffect(() => {
    const content = contentRef.current;
    const el = scrollRef.current;
    if (!content || !el) return;
    const ro = new ResizeObserver(() => {
      if (!restoredRef.current && pendingRestoreRef.current != null) { applyRestore(); return; }
      if (atBottomRef.current) el.scrollTop = el.scrollHeight;
      syncScrollState();
    });
    ro.observe(content);
    return () => ro.disconnect();
  }, [syncScrollState, applyRestore]);

  const handleMessagesScroll = syncScrollState;

  // Программный скролл в конец ленты (клик по плавающей кнопке)
  const scrollToBottom = () => {
    atBottomRef.current = true;
    setShowScrollDown(false);
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  };

  // Автоскролл при новых сообщениях / приходе истории. Layout-эффект: конец ленты
  // выставляется до отрисовки кадра, поэтому пролёта от начала не видно.
  useLayoutEffect(() => {
    if (!restoredRef.current && pendingRestoreRef.current != null) { applyRestore(); return; }
    if (atBottomRef.current) {
      const el = scrollRef.current;
      if (el) el.scrollTop = el.scrollHeight;
      setShowScrollDown(false);
    } else {
      setShowScrollDown(true);
    }
  }, [items, applyRestore]);

  // Финал восстановления после загрузки истории: даём ленте дорасти, но не дольше 5с —
  // дальше чат живёт в обычном режиме (в конце ленты, если восстановиться не удалось).
  useEffect(() => {
    if (isHistoryLoading || restoredRef.current) return;
    if (pendingRestoreRef.current == null) { restoredRef.current = true; return; }
    applyRestore();
    const raf = requestAnimationFrame(applyRestore);
    const done = window.setTimeout(() => {
      restoredRef.current = true;
      pendingRestoreRef.current = null;
      syncScrollState();
    }, 5000);
    return () => { cancelAnimationFrame(raf); clearTimeout(done); };
  }, [isHistoryLoading, applyRestore, syncScrollState]);

  return {
    bottomRef, scrollRef, contentRef, composerWrapRef, composerH,
    showScrollDown, scrolled, atBottomRef, handleMessagesScroll, scrollToBottom,
  };
}
