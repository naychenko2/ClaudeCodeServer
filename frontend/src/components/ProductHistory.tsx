import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { ChangelogDay, ChangelogItem, DaySummaryStub, ChangelogStatus } from '../types';
import { api } from '../lib/api';
import { C, FONT, R, MODAL_W } from '../lib/design';
import { EmptyState } from './EmptyState';
import { Toolbar } from './Toolbar';
import { IconButton, Modal, ModalActions } from './ui';

// Продуктовая история — «что мы делали и чем это полезно», по всем проектам.
// Одноколоночная лента по дням (Сегодня / Вчера / дата), карточки: что нового +
// польза + автор + проект. Без кода и diff — сводная продуктовая информация.

// Иконка-роль автора — чтобы «на глаз» различать, кто сделал (ненавязчиво, тегом).
// Известные закреплены по имени; новые авторы получают роль из пула детерминированно
// по имени (стабильно и без правки кода) — так у любого нового будет своя иконка.
const AUTHOR_EMOJI: Record<string, string> = {
  'Григорий': '🧑‍💼',
  'Андрей': '👨‍💻',
};
const ROLE_POOL = ['🧑‍🚀', '🥷', '🧑‍🎨', '🧑‍🍳', '🕵️', '🧑‍🏭', '🧑‍🌾', '🧑‍⚕️', '🧑‍🏫', '🧑‍✈️'];
function authorEmoji(name: string): string {
  if (AUTHOR_EMOJI[name]) return AUTHOR_EMOJI[name];
  let h = 0;
  for (const ch of name) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return ROLE_POOL[h % ROLE_POOL.length];
}

// «Сегодня» / «Вчера» / «2 июля» — заголовок секции дня
function dayLabel(date: string): string {
  const d = new Date(date + 'T00:00:00');
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const diffDays = Math.round((today.getTime() - d.getTime()) / 86_400_000);
  if (diffDays === 0) return 'Сегодня';
  if (diffDays === 1) return 'Вчера';
  if (diffDays === 2) return 'Позавчера';
  return d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', ...(d.getFullYear() !== today.getFullYear() ? { year: 'numeric' } : {}) });
}

// Русская плюрализация: 1 изменение / 2 изменения / 5 изменений
function plural(n: number, one: string, few: string, many: string): string {
  const abs = Math.abs(n) % 100;
  if (abs >= 11 && abs <= 14) return many;
  switch (abs % 10) {
    case 1: return one;
    case 2: case 3: case 4: return few;
    default: return many;
  }
}

export function ProductHistory({ isMobile, onClose }: {
  isMobile: boolean;
  onClose: () => void;
}) {
  const [days, setDays] = useState<DaySummaryStub[] | null>(null);      // null = загрузка списка
  const [daysError, setDaysError] = useState<string | null>(null);
  const [configStatus, setConfigStatus] = useState<ChangelogStatus | null>(null); // настроен ли источник (для пустого экрана)
  const [summaries, setSummaries] = useState<Map<string, ChangelogDay>>(new Map());
  const [loadingDays, setLoadingDays] = useState<Set<string>>(new Set());
  const [failedDays, setFailedDays] = useState<Set<string>>(new Set());   // дни, где сборка сводки не удалась (ретраи исчерпаны)
  const [selectedDate, setSelectedDate] = useState<string | null>(null); // выбранный в боковой навигации день
  const [windowDays, setWindowDays] = useState(0);                       // 0 = дефолт бэка (30)
  const [authorFilter, setAuthorFilter] = useState<string | null>(null); // null = все исполнители
  const [activeArea, setActiveArea] = useState<Record<string, string>>({}); // активная вкладка-область по дню
  const [reloadKey, setReloadKey] = useState(0);                          // bump → перезагрузка списка дней (после очистки)
  const [confirm, setConfirm] = useState<null | {                        // единый попап-подтверждение в стиле приложения
    title: string; body: string; confirmLabel: string; variant?: 'primary' | 'danger'; run: () => void;
  }>(null);
  const requestedRef = useRef<Set<string>>(new Set());
  const retryTimersRef = useRef<Set<ReturnType<typeof setTimeout>>>(new Set());
  // Токен активной загрузки по дню: при новой загрузке/регенерации старая цепочка
  // ретраев становится «устаревшей» и тихо прекращается (не применяет старый результат)
  const loadTokenRef = useRef(0);
  const dayTokenRef = useRef<Map<string, number>>(new Map());

  useEffect(() => () => { retryTimersRef.current.forEach(clearTimeout); }, []);

  // Закрытие по Escape
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  // ===== Загрузка списка дней =====
  useEffect(() => {
    let cancelled = false;
    setDays(null);
    setDaysError(null);
    requestedRef.current = new Set();
    api.history.days(windowDays)
      .then(d => {
        if (cancelled) return;
        setDays(d);
        // Выбираем первый (самый свежий) день — генерится только выбранный
        setSelectedDate(prev => prev && d.some(x => x.date === prev) ? prev : (d[0]?.date ?? null));
      })
      .catch(() => { if (!cancelled) setDaysError('Не удалось загрузить историю'); });
    return () => { cancelled = true; };
  }, [windowDays, reloadKey]);

  // Дней нет — уточняем у бэка, настроен ли источник (чтобы показать «донастрой», а не «пусто»)
  useEffect(() => {
    if (days && days.length === 0) {
      api.history.status().then(setConfigStatus).catch(() => setConfigStatus(null));
    }
  }, [days]);

  // ===== Ленивая загрузка сводок дней =====
  // Генерация большого дня на бэке идёт дольше HTTP-таймаута (FETCH_TIMEOUT ~30с) —
  // запрос рвётся, поэтому коротко ретраим, ДЕРЖА спиннер, пока не прогреется кеш.
  // Спиннер снимаем только на реальном результате/пределе (не мигаем на каждом обрыве),
  // а токен дня отсекает устаревшую цепочку, если день перезагрузили/перегенерировали.
  const loadDay = useCallback((date: string) => {
    if (requestedRef.current.has(date)) return;
    requestedRef.current.add(date);
    const myToken = ++loadTokenRef.current;
    dayTokenRef.current.set(date, myToken);
    setLoadingDays(prev => new Set(prev).add(date));
    setFailedDays(prev => { if (!prev.has(date)) return prev; const n = new Set(prev); n.delete(date); return n; });

    const isStale = () => dayTokenRef.current.get(date) !== myToken;
    const stopSpinner = () => setLoadingDays(prev => { const n = new Set(prev); n.delete(date); return n; });
    let attempts = 0;
    const attempt = () => {
      if (isStale()) return; // день перехватила новая загрузка — прекращаем
      api.history.day(date)
        .then(sum => {
          if (isStale()) return;
          setSummaries(prev => new Map(prev).set(date, sum));
          requestedRef.current.delete(date);
          stopSpinner();
        })
        .catch(() => {
          if (isStale()) return;
          attempts++;
          if (attempts > 40) {
            // ретраи исчерпаны — помечаем день как несобравшийся, чтобы показать ошибку,
            // а не вечное «в очереди»; спиннер гасим, метку загрузки снимаем
            requestedRef.current.delete(date);
            stopSpinner();
            setFailedDays(prev => new Set(prev).add(date));
            return;
          }
          const t = setTimeout(() => { retryTimersRef.current.delete(t); attempt(); }, 4000);
          retryTimersRef.current.add(t);
        });
    };
    attempt();
  }, []);

  // Генерим/грузим сводку только для выбранного дня
  useEffect(() => {
    if (selectedDate) loadDay(selectedDate);
  }, [selectedDate, loadDay]);

  // Перегенерация дня: сбрасываем кеш дня на бэке и грузим заново (через штатный loadDay).
  // Гасим текущую цепочку загрузки (delete токена → isStale), чтобы она не применила старое.
  const regenerateDay = useCallback((date: string) => {
    setSummaries(prev => { const n = new Map(prev); n.delete(date); return n; });
    requestedRef.current.delete(date);
    dayTokenRef.current.delete(date);
    api.history.invalidateDay(date).then(() => loadDay(date)).catch(() => loadDay(date));
  }, [loadDay]);

  // Полная очистка истории: сносим кеш на бэке, сбрасываем локальное состояние и перезагружаем дни
  const doClearAll = useCallback(() => {
    api.history.clear().finally(() => {
      setSummaries(new Map());
      setFailedDays(new Set());
      requestedRef.current = new Set();
      dayTokenRef.current = new Map();
      setReloadKey(k => k + 1);
    });
  }, []);

  // Спросить подтверждение перегенерации выбранного дня (генерация может быть долгой)
  const askRegenerate = useCallback((date: string) => {
    setConfirm({
      title: 'Обновить сводку дня?',
      body: 'Claude пересоберет сводку этого дня заново. Это может занять до пары минут.',
      confirmLabel: 'Обновить',
      variant: 'primary',
      run: () => regenerateDay(date),
    });
  }, [regenerateDay]);

  // Спросить подтверждение полной очистки истории
  const askClearAll = useCallback(() => {
    setConfirm({
      title: 'Очистить всю историю?',
      body: 'Все сводки будут удалены и собраны заново при следующем открытии дней.',
      confirmLabel: 'Очистить',
      variant: 'danger',
      run: doClearAll,
    });
  }, [doClearAll]);

  // Список исполнителей для фильтра — по алфавиту
  const authors = Array.from(new Set([
    ...Object.keys(AUTHOR_EMOJI),
    ...[...summaries.values()].flatMap(d => d.items).flatMap(i => i.authors),
  ])).sort((a, b) => a.localeCompare(b, 'ru'));

  // ===== Боковая навигация по дням (слева на десктопе / лента сверху на мобиле) =====
  const dayNav = days && days.length > 0 && (
    <DayCalendar days={days} selected={selectedDate} isMobile={isMobile}
      onSelect={setSelectedDate}
      onNeedOlder={() => setWindowDays(prev => (prev === 0 ? 120 : prev + 120))} />
  );

  // ===== Контент выбранного дня =====
  const selDay = days?.find(d => d.date === selectedDate);
  const selSum = selectedDate ? summaries.get(selectedDate) : undefined;
  const selLoading = selectedDate ? loadingDays.has(selectedDate) : false;
  const selFailed = selectedDate ? failedDays.has(selectedDate) : false;
  const selItems = selSum ? (authorFilter ? selSum.items.filter(it => it.authors.includes(authorFilter)) : selSum.items) : [];

  // Группы-области (вкладки категорий) и активная — вычисляем заранее, чтобы вкладки
  // можно было вынести в sticky-шапку отдельно от скроллящейся ленты пунктов
  const groups = selSum && selItems.length > 0 ? groupByArea(selItems) : [];
  const activeAreaName = groups.length
    ? (selDay && activeArea[selDay.date] && groups.some(g => g.area === activeArea[selDay.date])
        ? activeArea[selDay.date] : groups[0].area)
    : null;
  const shownGroup = groups.find(g => g.area === activeAreaName) ?? groups[0];

  const dayContent = (
    <div style={{ flex: 1, minWidth: 0, overflowY: 'auto', display: 'flex', flexDirection: 'column', padding: isMobile ? '14px 16px 30px' : '20px 32px 40px' }}>
      {days === null && !daysError && (
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, fontSize: 13 }}>Загрузка истории…</div>
      )}
      {daysError && (
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.danger, fontSize: 13 }}>{daysError}</div>
      )}
      {days?.length === 0 && (
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          {configStatus && !configStatus.configured ? (
            <EmptyState
              icon={<GearIcon size={26} />}
              title="Раздел не настроен"
              subtitle={configStatus.detail || 'Укажите источник changelog (git-репозиторий продукта) в настройках инстанса.'}
            />
          ) : (
            <EmptyState
              icon={<ClockIcon size={26} />}
              title="Пока пусто"
              subtitle="Как только появятся изменения — здесь будет сводка, что нового и чем это полезно"
            />
          )}
        </div>
      )}
      {selDay && (
        <div style={{ maxWidth: 900, width: '100%' }}>
            {/* Шапка дня: дата + фильтр + вкладки категорий. На телефоне зафиксирована
                (sticky) при скролле ленты пунктов — даты и категории всегда на виду */}
            <div style={isMobile ? {
              position: 'sticky', top: 0, zIndex: 3, background: C.bgMain,
              margin: '0 -16px', padding: '0 16px 2px',
            } : undefined}>
              {/* Заголовок выбранного дня + кнопка перегенерации */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: isMobile ? '4px 0 0' : 0, marginBottom: 14 }}>
                <span style={{ fontFamily: FONT.serif, fontSize: 22, fontWeight: 600, color: C.textHeading }}>
                  {dayLabel(selDay.date)}
                </span>
                {selSum && (
                  <span style={{ fontSize: 13, color: C.textMuted }}>
                    {selItems.length} {plural(selItems.length, 'обновление', 'обновления', 'обновлений')}
                  </span>
                )}
                <RegenButton spinning={selLoading} onClick={() => askRegenerate(selDay.date)} />
              </div>
              {/* Мини-фильтр по исполнителю — с количеством пунктов каждого за этот день */}
              {authors.length > 1 && (
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginBottom: groups.length ? 12 : 18 }}>
                  <FilterChip label="Все" count={selSum?.items.length} active={authorFilter === null} onClick={() => setAuthorFilter(null)} />
                  {authors.map(a => (
                    <FilterChip key={a} label={a} emoji={authorEmoji(a)}
                      count={selSum?.items.filter(it => it.authors.includes(a)).length}
                      active={authorFilter === a}
                      onClick={() => setAuthorFilter(prev => prev === a ? null : a)} />
                  ))}
                </div>
              )}
              {/* Вкладки категорий — часть sticky-шапки */}
              {groups.length > 0 && shownGroup && (
                <AreaTabs groups={groups} active={shownGroup.area}
                  onChange={area => setActiveArea(prev => ({ ...prev, [selDay.date]: area }))} />
              )}
            </div>
            {!selSum && selFailed && (
              <div style={{
                padding: '14px 16px', borderRadius: R.xl, background: C.bgCard,
                border: `1px solid ${C.border}`, fontSize: 13,
                display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap',
              }}>
                <span style={{ color: C.textSecondary }}>Не удалось собрать сводку за этот день.</span>
                <button onClick={() => loadDay(selDay.date)} style={{
                  fontFamily: 'inherit', fontSize: 12.5, fontWeight: 600, cursor: 'pointer',
                  padding: '5px 12px', borderRadius: 8, border: `1px solid ${C.accent}`,
                  background: C.accentLight, color: C.accent,
                }}>Попробовать снова</button>
              </div>
            )}
            {!selSum && !selFailed && (
              <div style={{
                padding: '16px', borderRadius: R.xl, background: C.bgCard,
                border: `1px dashed ${C.border}`, color: C.textMuted, fontSize: 13,
                display: 'flex', alignItems: 'center', gap: 10,
              }}>
                <Spinner />
                {selLoading ? 'Claude готовит сводку дня…' : 'В очереди на сводку…'}
              </div>
            )}
            {selSum && selItems.length === 0 && (
              <div style={{ padding: '10px 4px', color: C.textMuted, fontSize: 13 }}>
                {authorFilter ? `Нет изменений (${authorFilter})` : 'Заметных изменений нет'}
              </div>
            )}
            {/* Лента пунктов выбранной категории — скроллится под sticky-шапкой */}
            {groups.length > 0 && shownGroup && (
              <div key={shownGroup.area} style={{ position: 'relative', paddingLeft: 28, animation: 'cc-fade-in 0.2s ease' }}>
                <div style={{ position: 'absolute', left: 5, top: 6, bottom: 10, width: 2, background: C.divider }} />
                {shownGroup.list.map((item, i) => (
                  <TimelineNode key={i} item={item} last={i === shownGroup.list.length - 1} />
                ))}
              </div>
            )}
        </div>
        )}
    </div>
  );

  const feed = (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: isMobile ? 'column' : 'row' }}>
      {dayNav}
      {dayContent}
    </div>
  );

  return (
    <div style={{
      position: 'fixed', inset: 0, zIndex: 1000, display: 'flex', flexDirection: 'column',
      background: C.bgMain, fontFamily: FONT.sans,
    }}>
      <Toolbar isMobile={isMobile}>
        {/* Левая секция — логотип (как на главной; скрыт на мобилке) */}
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center' }}>
          {!isMobile && (
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
              <img src="/favicon.svg" alt="" width={30} height={30} style={{ display: 'block', flexShrink: 0 }} />
              <span style={{ fontFamily: FONT.serif, fontSize: 18, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap' }}>
                Claude Home
              </span>
            </div>
          )}
        </div>
        {/* Центр — заголовок */}
        <span style={{ fontFamily: FONT.serif, fontSize: 17, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap' }}>
          Что нового
        </span>
        {/* Правая секция — очистить историю + закрыть */}
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 4 }}>
          <IconButton size={isMobile ? 'lg' : 'md'} onClick={askClearAll} title="Очистить всю историю">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <polyline points="3 6 5 6 21 6" />
              <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
              <line x1="10" y1="11" x2="10" y2="17" /><line x1="14" y1="11" x2="14" y2="17" />
            </svg>
          </IconButton>
          <IconButton size={isMobile ? 'lg' : 'md'} onClick={onClose} title="Закрыть">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
              <line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" />
            </svg>
          </IconButton>
        </div>
      </Toolbar>
      {feed}
      {/* Единый попап-подтверждение (обновление дня / очистка истории) в стиле приложения */}
      {confirm && (
        <Modal
          width={MODAL_W.confirm}
          title={confirm.title}
          subtitle={confirm.body}
          onClose={() => setConfirm(null)}
          closeOnBackdrop
          footer={
            <ModalActions
              confirmLabel={confirm.confirmLabel}
              confirmVariant={confirm.variant ?? 'primary'}
              onConfirm={() => { confirm.run(); setConfirm(null); }}
              cancelLabel="Отмена"
              onCancel={() => setConfirm(null)}
            />
          }
        />
      )}
    </div>
  );
}

// Группировка пунктов по области (месту изменения) с сохранением порядка появления
function groupByArea(items: ChangelogItem[]): { area: string; list: ChangelogItem[] }[] {
  const order: string[] = [];
  const map = new Map<string, ChangelogItem[]>();
  for (const it of items) {
    const area = it.area || 'Прочее';
    if (!map.has(area)) { map.set(area, []); order.push(area); }
    map.get(area)!.push(it);
  }
  return order.map(area => ({ area, list: map.get(area)! }));
}

// Эмодзи группы-области: берём у первого пункта (репрезентативно для раздела)
function groupEmoji(list: ChangelogItem[]): string {
  return list.find(i => i.emoji)?.emoji || '📋';
}

// Бейдж значимости по оценке 1-5: ярлык + цвет (обоснование — в пузыре-реплике при наведении).
// Цвета разведены по разным семантическим токенам, чтобы статусы чётко различались:
// Хит — красный (топ), Круто — зелёный (хорошо), Заметно — синий (норм), По мелочи — серый.
function scoreBadge(score: number): { label: string; bg: string; color: string } {
  if (score >= 5) return { label: 'Хит', bg: C.dangerBg, color: C.dangerText };
  if (score >= 4) return { label: 'Круто', bg: C.successBg, color: C.successText };
  if (score >= 3) return { label: 'Заметно', bg: C.infoBg, color: C.info };
  return { label: 'По мелочи', bg: C.bgPanel, color: C.textMuted };
}

// Маленькая иконка Клауда (favicon) — ставится внутрь бейджа значимости и в пузырь-реплику
function ClaudeMark({ size = 11 }: { size?: number }) {
  return <img src="/favicon.svg" alt="" width={size} height={size} style={{ display: 'block', flexShrink: 0 }} />;
}

// Один узел таймлайна: маркер-кружочек + заголовок + справа колонка (автор сверху, бейдж
// значимости под ним). При наведении на бейдж он «говорит» обоснование в пузыре-реплике.
function TimelineNode({ item, last }: { item: ChangelogItem; last: boolean }) {
  const b = scoreBadge(item.score);
  return (
    <div style={{
      position: 'relative', padding: '2px 0 13px',
      borderBottom: last ? 'none' : `1px solid ${C.divider}`,
      marginBottom: last ? 0 : 13,
    }}>
      {/* Маркер-кружочек по центру линии — единый акцентный цвет */}
      <div style={{
        position: 'absolute', left: -26, top: 7, width: 8, height: 8, borderRadius: '50%',
        background: C.accent, boxSizing: 'border-box',
      }} />
      <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, alignItems: 'flex-start' }}>
        {/* Левая колонка: заголовок и сразу под ним описание (чтобы высота правой колонки
            автор+бейдж не раздвигала расстояние между названием и описанием) */}
        <div style={{ minWidth: 0 }}>
          <span style={{ fontSize: 14.5, fontWeight: 600, color: C.textHeading, lineHeight: 1.4 }}>
            {item.title}
          </span>
          {item.benefit && (
            <div style={{ fontSize: 12, color: C.textSecondary, lineHeight: 1.45, marginTop: 3, maxWidth: 640 }}>
              {item.benefit}
            </div>
          )}
        </div>
        {/* Правая колонка: бейдж значимости сверху, исполнитель под ним */}
        <span style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 5, flexShrink: 0 }}>
          {/* Бейдж значимости — с иконкой Клауда; при наведении на него «говорит» обоснование */}
          <ScoreBadge badge={b} reason={item.scoreReason} />
          {item.authors.length > 0 && (
            <span style={{ display: 'flex', gap: 10 }}>
              {item.authors.map(a => (
                <span key={a} title={a} style={{
                  display: 'inline-flex', alignItems: 'center', gap: 4,
                  fontSize: 11.5, color: C.textMuted, whiteSpace: 'nowrap',
                }}>
                  <span style={{ fontSize: 12 }}>{authorEmoji(a)}</span>{a}
                </span>
              ))}
            </span>
          )}
        </span>
      </div>
    </div>
  );
}

// Бейдж значимости + всплывающая рядом реплика (только при наведении на сам бейдж).
// Позицию бейджа фиксируем на hover и отдаём пузырю — он рисуется в portal (см. ScoreSpeech).
function ScoreBadge({ badge, reason }: { badge: { label: string; bg: string; color: string }; reason: string }) {
  const ref = useRef<HTMLSpanElement>(null);
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  // Пузырь позиционируется по зафиксированному rect (fixed). Если прокрутить/изменить
  // размер, пока он открыт, он «отклеится» от бейджа — закрываем его на scroll/resize
  // (также страхует от залипания на тач-устройствах, где нет mouseleave).
  useEffect(() => {
    if (!anchor) return;
    const close = () => setAnchor(null);
    window.addEventListener('scroll', close, true);
    window.addEventListener('resize', close);
    return () => { window.removeEventListener('scroll', close, true); window.removeEventListener('resize', close); };
  }, [anchor]);
  return (
    <span
      ref={ref}
      onMouseEnter={() => { const el = ref.current?.firstElementChild; if (el) setAnchor(el.getBoundingClientRect()); }}
      onMouseLeave={() => setAnchor(null)}
      style={{ display: 'inline-flex' }}
    >
      <span style={{
        display: 'inline-flex', alignItems: 'center', gap: 4,
        fontSize: 10, fontWeight: 700, letterSpacing: '0.03em', textTransform: 'uppercase',
        borderRadius: 8, padding: '2px 8px 2px 6px', whiteSpace: 'nowrap',
        background: badge.bg, color: badge.color, cursor: reason ? 'pointer' : 'default',
        // Кольцо-обводка цветом бейджа: постоянно лёгкое (намёк «я интерактивный»),
        // при наведении усиливается — сигнал, что тег наводящийся и покажет реплику
        boxShadow: anchor ? `0 0 0 1.5px ${badge.color}` : `0 0 0 1px ${badge.color}44`,
        transition: 'box-shadow 0.15s',
      }}>
        <ClaudeMark size={11} />{badge.label}
      </span>
      {anchor && reason && <ScoreSpeech anchor={anchor} text={reason} />}
    </span>
  );
}

// Речевой пузырь-реплика Claude, будто он сам оценивает задачу. Рисуется в portal с
// position:fixed от координат бейджа — так он НЕ влияет на прокрутку контейнера (иначе у
// последнего пункта раздувал scrollHeight и страница прыгала). Открывается вниз, а если
// снизу мало места — вверх. Хвостик указывает на бейдж.
function ScoreSpeech({ anchor, text }: { anchor: DOMRect; text: string }) {
  const GAP = 8;
  const ESTIMATED_H = 110; // прикидка высоты пузыря для выбора направления
  const openUp = anchor.bottom + GAP + ESTIMATED_H > window.innerHeight;
  const rightOffset = Math.max(8, window.innerWidth - anchor.right);
  return createPortal(
    <div style={{
      position: 'fixed', zIndex: 1100, width: 'max-content', maxWidth: Math.min(300, window.innerWidth - 16),
      right: rightOffset,
      ...(openUp
        ? { bottom: window.innerHeight - anchor.top + GAP }
        : { top: anchor.bottom + GAP }),
      background: '#fff', border: `1px solid ${C.border}`, borderRadius: 12,
      boxShadow: '0 8px 24px rgba(60, 50, 40, 0.14)', padding: '9px 12px',
      display: 'flex', gap: 8, alignItems: 'flex-start',
      animation: 'cc-fade-in 0.14s ease', pointerEvents: 'none',
    }}>
      {/* Хвостик-уголок к бейджу: сверху пузыря (открыт вниз) либо снизу (открыт вверх) */}
      <div style={{
        position: 'absolute', right: 14, width: 11, height: 11, background: '#fff',
        transform: 'rotate(45deg)',
        ...(openUp
          ? { bottom: -6, borderRight: `1px solid ${C.border}`, borderBottom: `1px solid ${C.border}` }
          : { top: -6, borderLeft: `1px solid ${C.border}`, borderTop: `1px solid ${C.border}` }),
      }} />
      <ClaudeMark size={15} />
      <div>
        <div style={{ fontSize: 10.5, fontWeight: 700, color: C.accent, marginBottom: 2 }}>Claude</div>
        <div style={{ fontSize: 12, lineHeight: 1.45, color: C.textSecondary, fontStyle: 'italic' }}>{text}</div>
      </div>
    </div>,
    document.body,
  );
}

// Календарь-навигация по дням: месяц с сеткой, дни с изменениями кликабельны и
// подсвечены, выбранный — accent. Стрелки листают месяцы; уход в прошлое за пределы
// загруженного окна триггерит догрузку (onNeedOlder).
const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
function DayCalendar({ days, selected, onSelect, onNeedOlder, isMobile }: {
  days: DaySummaryStub[];
  selected: string | null;
  onSelect: (date: string) => void;
  onNeedOlder: () => void;
  isMobile: boolean;
}) {
  const byDate = new Map(days.map(d => [d.date, d]));
  const todayIso = (() => { const t = new Date(); return `${t.getFullYear()}-${String(t.getMonth() + 1).padStart(2, '0')}-${String(t.getDate()).padStart(2, '0')}`; })();
  // Показываемый месяц (yyyy-MM) — по выбранному дню или первому доступному
  const [ym, setYm] = useState<string>(() => (selected || days[0]?.date || todayIso).slice(0, 7));
  // Когда выбирают день из другого месяца (напр. смена окна) — подстроить показ
  useEffect(() => { if (selected) setYm(selected.slice(0, 7)); }, [selected]);

  const [year, month] = ym.split('-').map(Number); // month 1..12
  const first = new Date(year, month - 1, 1);
  const daysInMonth = new Date(year, month, 0).getDate();
  const leadPad = (first.getDay() + 6) % 7; // сколько пустых до понедельника
  const oldestLoaded = days.length ? days[days.length - 1].date : todayIso;

  const shiftMonth = (delta: number) => {
    const d = new Date(year, month - 1 + delta, 1);
    const nextYm = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
    setYm(nextYm);
    // Ушли раньше самого старого загруженного дня — просим догрузить историю
    if (`${nextYm}-31` < oldestLoaded) onNeedOlder();
  };

  const cells: (number | null)[] = [...Array(leadPad).fill(null), ...Array.from({ length: daysInMonth }, (_, i) => i + 1)];
  const monthLabel = first.toLocaleDateString('ru-RU', { month: 'long', year: 'numeric' });

  return (
    <div style={{
      flexShrink: 0,
      ...(isMobile
        ? { padding: '10px 14px', borderBottom: `1px solid ${C.divider}`, background: C.bgPanel }
        : { width: 268, overflowY: 'auto', padding: '14px 14px', borderRight: `1px solid ${C.divider}`, background: C.bgPanel }),
    }}>
      {/* Шапка месяца + стрелки */}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 10 }}>
        <ArrowBtn dir="left" onClick={() => shiftMonth(-1)} />
        <span style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, textTransform: 'capitalize' }}>
          {monthLabel}
        </span>
        <ArrowBtn dir="right" onClick={() => shiftMonth(1)} />
      </div>
      {/* Дни недели */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)', gap: 2, marginBottom: 4 }}>
        {WEEKDAYS.map(w => (
          <div key={w} style={{ textAlign: 'center', fontSize: 10.5, fontWeight: 600, color: C.textMuted, padding: '2px 0' }}>{w}</div>
        ))}
      </div>
      {/* Сетка дней */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)', gap: 2 }}>
        {cells.map((day, i) => {
          if (day === null) return <div key={i} />;
          const date = `${ym}-${String(day).padStart(2, '0')}`;
          const stub = byDate.get(date);
          const has = !!stub;
          const isSel = date === selected;
          const isToday = date === todayIso;
          return (
            <button key={i}
              onClick={has ? () => onSelect(date) : undefined}
              disabled={!has}
              title={has ? `${stub!.commitCount} ${plural(stub!.commitCount, 'изменение', 'изменения', 'изменений')}` : undefined}
              style={{
                position: 'relative', height: 32, borderRadius: 8, fontFamily: 'inherit',
                fontSize: 13, fontWeight: isSel ? 700 : (has ? 600 : 400),
                cursor: has ? 'pointer' : 'default',
                background: isSel ? C.accent : 'transparent',
                color: isSel ? '#fff' : (has ? C.textHeading : C.textMuted),
                opacity: has ? 1 : 0.45,
                border: isToday && !isSel ? `1px solid ${C.accent}` : '1px solid transparent',
                transition: 'background 0.12s',
              }}
              onMouseEnter={e => { if (has && !isSel) e.currentTarget.style.background = C.bgSelected; }}
              onMouseLeave={e => { if (has && !isSel) e.currentTarget.style.background = 'transparent'; }}
            >
              {day}
              {/* Точка-маркер у дней с изменениями (кроме выбранного) */}
              {has && !isSel && (
                <span style={{ position: 'absolute', bottom: 4, left: '50%', transform: 'translateX(-50%)', width: 4, height: 4, borderRadius: '50%', background: C.accent }} />
              )}
            </button>
          );
        })}
      </div>
    </div>
  );
}

// Кнопка перегенерации дня: иконка обновления, крутится пока идет генерация
function RegenButton({ spinning, onClick }: { spinning: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} disabled={spinning} title="Собрать сводку заново"
      style={{
        marginLeft: 'auto', width: 30, height: 30, borderRadius: 8, border: 'none',
        background: 'none', cursor: spinning ? 'default' : 'pointer', flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textSecondary,
      }}
      onMouseEnter={e => { if (!spinning) e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}>
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.1" strokeLinecap="round" strokeLinejoin="round"
        style={spinning ? { animation: 'cc-spin 0.8s linear infinite' } : undefined}>
        <path d="M23 4v6h-6" /><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" />
      </svg>
    </button>
  );
}

function ArrowBtn({ dir, onClick }: { dir: 'left' | 'right'; onClick: () => void }) {
  return (
    <button onClick={onClick} style={{
      width: 26, height: 26, borderRadius: 7, border: 'none', background: 'none', cursor: 'pointer',
      display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textSecondary,
    }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}>
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round"
        style={{ transform: dir === 'right' ? 'none' : 'rotate(180deg)' }}>
        <path d="M9 6l6 6-6 6" />
      </svg>
    </button>
  );
}

// Вкладки-области: одна строка с горизонтальным скроллом (без видимого скроллбара),
// мягкие fade-края когда есть куда скроллить, автоскролл к активной. Подчёркивание accent.
function AreaTabs({ groups, active, onChange }: {
  groups: { area: string; list: ChangelogItem[] }[];
  active: string;
  onChange: (area: string) => void;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const activeRef = useRef<HTMLButtonElement>(null);
  const [fade, setFade] = useState({ left: false, right: false });

  const updateFade = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    setFade({
      left: el.scrollLeft > 4,
      right: el.scrollLeft + el.clientWidth < el.scrollWidth - 4,
    });
  }, []);
  useEffect(() => { updateFade(); }, [groups, updateFade]);
  // Прокручиваем к активной вкладке, чтобы она была в зоне видимости
  useEffect(() => {
    activeRef.current?.scrollIntoView({ inline: 'nearest', block: 'nearest', behavior: 'smooth' });
  }, [active]);
  // Вертикальное колесо → горизонтальный скролл (удобно на десктопе)
  const onWheel = (e: React.WheelEvent<HTMLDivElement>) => {
    const el = scrollRef.current;
    if (el && Math.abs(e.deltaY) > Math.abs(e.deltaX)) el.scrollLeft += e.deltaY;
  };

  return (
    <div style={{ position: 'relative', marginBottom: 14, borderBottom: `1px solid ${C.border}` }}>
      <div ref={scrollRef} className="cc-scroll-x" onScroll={updateFade} onWheel={onWheel}
        style={{ display: 'flex', gap: 2, overflowX: 'auto' }}>
        {groups.map(g => {
          const on = g.area === active;
          return (
            <button key={g.area} ref={on ? activeRef : undefined} onClick={() => onChange(g.area)}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 6, cursor: 'pointer', flexShrink: 0,
                fontSize: 13.5, fontWeight: 600, fontFamily: 'inherit',
                padding: '8px 12px', border: 'none', background: 'none',
                color: on ? C.accent : C.textMuted, whiteSpace: 'nowrap',
                borderBottom: `2px solid ${on ? C.accent : 'transparent'}`, marginBottom: -1,
                transition: 'color 0.2s, border-color 0.2s',
              }}>
              <span style={{ fontSize: 14 }}>{groupEmoji(g.list)}</span>
              {g.area}
              <span style={{ fontSize: 11, opacity: 0.7 }}>{g.list.length}</span>
            </button>
          );
        })}
      </div>
      {/* Мягкие fade-края — намёк, что можно скроллить дальше */}
      {fade.left && (
        <div style={{ position: 'absolute', left: 0, top: 0, bottom: 1, width: 36, pointerEvents: 'none', background: `linear-gradient(90deg, ${C.bgMain}, transparent)` }} />
      )}
      {fade.right && (
        <div style={{ position: 'absolute', right: 0, top: 0, bottom: 1, width: 36, pointerEvents: 'none', background: `linear-gradient(270deg, ${C.bgMain}, transparent)` }} />
      )}
    </div>
  );
}

// Чип фильтра по исполнителю: активный — accent-обводка/фон, иначе приглушённый
function FilterChip({ label, emoji, count, active, onClick }: { label: string; emoji?: string; count?: number; active: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 5, cursor: 'pointer',
        fontSize: 12.5, fontWeight: 600, fontFamily: 'inherit',
        padding: '4px 11px', borderRadius: 14,
        background: active ? C.accentLight : C.bgCard,
        color: active ? C.accent : C.textSecondary,
        border: `1px solid ${active ? C.accent : C.border}`,
        transition: 'background 0.12s, border-color 0.12s',
      }}
    >
      {emoji && <span style={{ fontSize: 13 }}>{emoji}</span>}
      {label}
      {count !== undefined && <span style={{ fontSize: 11, opacity: 0.65 }}>{count}</span>}
    </button>
  );
}

function Spinner() {
  return (
    <span style={{
      width: 14, height: 14, borderRadius: '50%', flexShrink: 0,
      border: `2px solid ${C.border}`, borderTopColor: C.accent,
      animation: 'cc-spin 0.8s linear infinite', display: 'inline-block',
    }} />
  );
}

function ClockIcon({ size = 22 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8" />
      <path d="M3 3v5h5" />
      <polyline points="12 7 12 12 15 14" />
    </svg>
  );
}

// Иконка-шестерёнка для состояния «раздел не настроен»
function GearIcon({ size = 22 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
    </svg>
  );
}
