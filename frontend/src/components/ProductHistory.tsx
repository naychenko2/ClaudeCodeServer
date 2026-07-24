import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { AlertTriangle, ChevronRight, History, RefreshCw, Settings, Trash2 } from 'lucide-react';
import type { AuthState, ChangelogDay, ChangelogGeneration, ChangelogItem, DaySummaryStub, ChangelogStatus } from '../types';
import { api } from '../lib/api';
import { C, FONT, FS, R, MODAL_W, SHADOW } from '../lib/design';
import { useIsMobile } from '../lib/breakpoints';
import { EmptyState } from './EmptyState';
import { HubHeader } from './HubHeader';
import { CanvasBackdrop } from './ui/CanvasBackdrop';
import type { HubTabValue } from './HubTabs';
import { Modal, ModalActions } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { AUTHOR_EMOJI, authorEmoji } from '../lib/authorEmoji';

// Продуктовая история — «что мы делали и чем это полезно», по всем проектам.
// Одноколоночная лента по дням (Сегодня / Вчера / дата), карточки: что нового +
// польза + автор + проект. Без кода и diff — сводная продуктовая информация.

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

// Порог «хита» дня: пункты с такой оценкой и выше уходят в hero-карточки сверху,
// остальные — в компактный список секциями по областям (принцип релиз-нот:
// главное — крупно, остальное — плотно)
export const HERO_SCORE = 4;
// Показывать ли кнопку «Очистить всю историю» в шапке дня. Пока скрыта: операция
// разовая (сносит все сводки) и в повседневной работе не нужна. Логика очистки
// (doClearAll / askClearAll / DELETE /api/history) остаётся рабочей.
const SHOW_CLEAR_ALL = false;
// Не больше стольких hero-карточек — иначе «главное» перестаёт быть главным
const HERO_MAX = 4;

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

// --- Форматирование расхода на сборку сводки (плашка внизу дня) ---

function fmtDuration(ms: number): string {
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s} с`;
  const m = Math.floor(s / 60);
  const rest = s % 60;
  return rest ? `${m} мин ${rest} с` : `${m} мин`;
}

function fmtTokens(n: number): string {
  if (n < 1000) return String(n);
  const k = n / 1000;
  return `${k.toFixed(k < 10 ? 1 : 0).replace('.', ',')} тыс.`;
}

// Доли цента показываем порогом, а не нулями: «$0,000» читается как «бесплатно»
function fmtCost(usd: number): string {
  if (usd <= 0) return '$0';
  if (usd < 0.001) return '< $0,001';
  return `$${usd.toFixed(usd < 1 ? 3 : 2).replace('.', ',')}`;
}

// Полный id модели («claude-haiku-4-5-20251001») в подписи не нужен — хватает тира
function shortModel(m: string | null | undefined): string | null {
  if (!m) return null;
  const tier = /(opus|sonnet|haiku|fable)/i.exec(m);
  return tier ? tier[1].toLowerCase() : m;
}

export function ProductHistory({ isMobile, onClose, auth, onLogout, onHubTab }: {
  isMobile: boolean;
  onClose: () => void;
  // Шапка страницы — общий HubHeader (как у «Уведомлений» и прочих разделов):
  // логотип, разделы, аватар. Переход в раздел закрывает эту страницу.
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
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
      body: 'AI пересоберет сводку этого дня заново. Это может занять до пары минут.',
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

  // Релиз-ноты дня: «хиты» (score ≥ HERO_SCORE) — hero-карточками сверху,
  // остальное — компактный список секциями по областям. Всё от selItems
  // (после фильтра по автору) — фильтр сужает и hero, и список.
  const heroes = [...selItems].sort((a, b) => b.score - a.score)
    .filter(i => i.score >= HERO_SCORE).slice(0, HERO_MAX);
  const rest = selItems.filter(i => !heroes.includes(i));
  const groups = rest.length > 0 ? groupByArea(rest) : [];

  const dayContent = (
    <div style={{ flex: 1, minWidth: 0, overflowY: 'auto', display: 'flex', flexDirection: 'column', padding: isMobile ? '10px 16px 30px' : '14px 32px 40px' }}>
      {/* Заголовок страницы с действиями — как у «Уведомлений»: serif-название слева,
          действия справа. Живёт В колонке контента (а не во всю ширину): иначе на
          десктопе он висел бы над календарём и не совпадал с текстом дня по левому краю */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 14 }}>
        <span style={{
          fontFamily: FONT.serif, fontSize: FS.h2, fontWeight: 700,
          color: C.textHeading, letterSpacing: '-0.3px', whiteSpace: 'nowrap',
        }}>
          Что нового
        </span>
      </div>
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
              icon={<Settings size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
              title="Раздел не настроен"
              subtitle={configStatus.detail || 'Укажите источник changelog (git-репозиторий продукта) в настройках инстанса.'}
            />
          ) : (
            <EmptyState
              icon={<History size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
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
                {/* Действия дня — справа. Кнопка «Очистить всю историю» пока скрыта
                    (SHOW_CLEAR_ALL): операция разовая и рискованная, а место в шапке дня
                    занимала постоянно. Вернуть — поменять флаг, код очистки на месте. */}
                <span style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 2 }}>
                  {SHOW_CLEAR_ALL && (
                    <HintButton hint="Очистить всю историю" onClick={askClearAll}>
                      <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                    </HintButton>
                  )}
                  <HintButton hint="Собрать сводку заново" disabled={selLoading}
                    onClick={() => askRegenerate(selDay.date)}>
                    <RefreshCw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE}
                      style={selLoading ? { animation: 'cc-spin 0.8s linear infinite' } : undefined} />
                  </HintButton>
                </span>
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
                {selLoading ? 'AI готовит сводку дня…' : 'В очереди на сводку…'}
              </div>
            )}
            {/* Сводка не собралась — честно говорим об этом и как чинить, вместо того
                чтобы выдавать сырые subject'ы коммитов за настоящую сводку */}
            {selSum?.degraded && (
              <DegradedNotice reason={selSum.degradedReason} onRetry={() => askRegenerate(selDay.date)} />
            )}
            {selSum && selItems.length === 0 && (
              <div style={{ padding: '10px 4px', color: C.textMuted, fontSize: 13 }}>
                {authorFilter ? `Нет изменений (${authorFilter})` : 'Заметных изменений нет'}
              </div>
            )}
            {/* Hero-карточки: хиты дня крупно, сеткой в две колонки (на мобилке — в одну) */}
            {heroes.length > 0 && (
              <div style={{
                animation: 'cc-fade-in 0.2s ease', display: 'flex', flexDirection: 'column', gap: 10,
                marginBottom: groups.length > 0 ? 22 : 0,
              }}>
                {heroes.map((item, i) => <HeroCard key={i} item={item} />)}
              </div>
            )}
            {/* Остальное — компактный список секциями по областям, без таймлайна и бейджей:
                значимость уже передана позицией (хиты выше, крупно) */}
            {groups.length > 0 && (
              // Каждая группа — полупрозрачный «лист» (тон ответа Claude в чате):
              // иначе компактные списки сливались бы с дудл-фоном страницы
              <div style={{ animation: 'cc-fade-in 0.2s ease' }}>
                {groups.map((g, gi) => (
                  <div key={g.area} style={{
                    marginTop: gi === 0 ? 0 : 10,
                    background: C.msgBg, borderRadius: R.xl, padding: '4px 12px 8px',
                  }}>
                    <SectionHeader area={g.area} list={g.list} />
                    {g.list.map((item, i) => <CompactRow key={i} item={item} />)}
                  </div>
                ))}
              </div>
            )}
            {/* Чем обошлась сборка этой сводки. Показываем и у degraded-дня: неудачный
                вызов тоже потратил время и токены. Нет у старых записей кеша — они
                сгенерены до появления метрик, там плашки просто не будет */}
            {selSum?.generation && <GenerationFooter gen={selSum.generation} />}
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
      background: C.bgMain, fontFamily: FONT.sans, isolation: 'isolate',
    }}>
      {/* Дудл-фон на всю страницу — от самого верха окна, шапка лежит на нём */}
      <CanvasBackdrop />
      {/* Шапка — та же, что у остальных разделов (логотип, разделы, аватар):
          уход в любой раздел закрывает страницу */}
      <HubHeader value="home" onTab={onHubTab} auth={auth} onLogout={onLogout} historyActive />
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

// Hero-карточка хита дня — горизонтальная (карточки идут одной колонкой во всю ширину,
// поэтому раскладка в строку: эмодзи слева, текст по центру, бейдж и автор справа —
// так карточка низкая, а не башня). Обоснование оценки Claude «говорит» в пузыре-реплике
// при наведении на бейдж (в самой карточке его нет — не занимает высоту).
function HeroCard({ item }: { item: ChangelogItem }) {
  const b = scoreBadge(item.score);
  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: 14,
      padding: '12px 14px', boxShadow: SHADOW.card, minWidth: 0,
      display: 'flex', alignItems: 'flex-start', gap: 12,
    }}>
      <span style={{
        width: 34, height: 34, borderRadius: 10, background: C.accentLight, flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: 18,
      }}>
        {item.emoji}
      </span>
      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ fontFamily: FONT.serif, fontSize: 15.5, fontWeight: 600, color: C.textHeading, lineHeight: 1.35 }}>
          {item.title}
        </div>
        {item.benefit && (
          <div style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.45, marginTop: 3 }}>
            {item.benefit}
          </div>
        )}
      </div>
      <span style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 5, flexShrink: 0 }}>
        <ScoreBadge badge={b} reason={item.scoreReason} />
        <AuthorIcons authors={item.authors} withNames />
      </span>
    </div>
  );
}

// Бейдж значимости + всплывающая рядом реплика (только при наведении на сам бейдж).
// Позицию бейджа фиксируем на hover и отдаём пузырю — он рисуется в portal (см. ScoreSpeech).
// Экспортируется: тем же бейджем помечаются хиты в виджете «Что нового» на дашборде.
export function ScoreBadge({ badge, reason }: { badge: { label: string; bg: string; color: string }; reason: string }) {
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
        borderRadius: 8, padding: '3px 9px 3px 7px', whiteSpace: 'nowrap',
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

// Речевой пузырь-реплика Claude, будто он сам оценивает изменение. Рисуется в portal с
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
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: 12,
      boxShadow: SHADOW.dropdown, padding: '9px 12px',
      display: 'flex', gap: 8, alignItems: 'flex-start',
      animation: 'cc-fade-in 0.14s ease', pointerEvents: 'none',
    }}>
      {/* Хвостик-уголок к бейджу: сверху пузыря (открыт вниз) либо снизу (открыт вверх) */}
      <div style={{
        position: 'absolute', right: 14, width: 11, height: 11, background: C.bgWhite,
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

// Иконки-роли авторов пункта; withNames — с подписью имени (hero), иначе только
// эмодзи с тултипом (компактный список)
function AuthorIcons({ authors, withNames }: { authors: string[]; withNames?: boolean }) {
  if (authors.length === 0) return null;
  return (
    <span style={{ display: 'inline-flex', gap: withNames ? 10 : 3, flexShrink: 0 }}>
      {authors.map(a => (
        <span key={a} title={a} style={{
          display: 'inline-flex', alignItems: 'center', gap: 4,
          fontSize: 11.5, color: C.textMuted, whiteSpace: 'nowrap',
        }}>
          <span style={{ fontSize: 12 }}>{authorEmoji(a)}</span>
          {withNames && a}
        </span>
      ))}
    </span>
  );
}

// Компактная строка «прочего» улучшения: буллет + заголовок с иконкой автора сразу
// после него, под ним польза отдельной строкой. Отступ слева (marginLeft) подводит
// пункты под заголовок категории — визуально они «внутри» неё. Без эмодзи у пунктов
// (осталось только у категории — иначе рябит), без бейджей и разделителей.
function CompactRow({ item }: { item: ChangelogItem }) {
  return (
    <div style={{ display: 'flex', alignItems: 'flex-start', gap: 10, padding: '5px 0', marginLeft: 22, minWidth: 0 }}>
      {/* Буллет по центру первой строки заголовка */}
      <span style={{
        width: 5, height: 5, borderRadius: '50%', background: C.textMuted,
        flexShrink: 0, marginTop: 8,
      }} />
      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading, lineHeight: 1.4 }}>
          {item.title}
          {/* Автор — сразу за заголовком (а не у правого края): взгляд не бегает
              через всю строку, чтобы понять, чьё изменение */}
          <span style={{ marginLeft: 7, verticalAlign: 'baseline' }}>
            <AuthorIcons authors={item.authors} />
          </span>
        </div>
        {item.benefit && (
          <div style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.45, marginTop: 2 }}>
            {item.benefit}
          </div>
        )}
      </div>
    </div>
  );
}

// Плашка «сводка не собралась»: показывает причину и как починить. Без неё продукт
// молча выдаёт сырые коммиты за сводку — на этом легко потерять вечер, ища баг не там.
function DegradedNotice({ reason, onRetry }: { reason?: string; onRetry: () => void }) {
  return (
    <div style={{
      display: 'flex', alignItems: 'flex-start', gap: 10, marginBottom: 16,
      padding: '11px 14px', borderRadius: R.xl,
      background: C.warningBg, border: `1px solid ${C.warning}`,
    }}>
      <AlertTriangle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.warning}
        style={{ flexShrink: 0, marginTop: 1 }} />
      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ fontSize: 13, fontWeight: 600, color: C.warningText, marginBottom: 2 }}>
          Сводка не собрана
        </div>
        <div style={{ fontSize: 12.5, lineHeight: 1.45, color: C.textSecondary }}>
          {reason || 'Показаны сырые коммиты вместо продуктовой сводки.'}
        </div>
      </div>
      <button onClick={onRetry} style={{
        flexShrink: 0, fontFamily: 'inherit', fontSize: 12.5, fontWeight: 600, cursor: 'pointer',
        padding: '5px 12px', borderRadius: 8, border: `1px solid ${C.accent}`,
        background: C.accentLight, color: C.accent,
      }}>Обновить</button>
    </div>
  );
}

// Во сколько обошлась эта страница: время ожидания, токены и деньги. Цена генерации
// обычно невидима — а тут она прямо под сводкой, которую оплатила.
function GenerationFooter({ gen }: { gen: ChangelogGeneration }) {
  const totalIn = gen.inputTokens + gen.cacheCreationTokens + gen.cacheReadTokens;
  const total = totalIn + gen.outputTokens;
  const model = shortModel(gen.model);
  const when = new Date(gen.generatedAt);
  const whenText = isNaN(when.getTime()) ? null
    : when.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });

  const dot = <span style={{ color: C.border }}>·</span>;
  return (
    // «Нижняя губа» (как полоса контролов под композером): опаковая плашка со
    // скруглением, чтобы служебная строка не висела на дудл-фоне
    <div style={{
      marginTop: 16, padding: '9px 14px',
      background: C.bgMain, border: `1px solid ${C.borderLight}`, borderRadius: R.xxl,
      display: 'flex', alignItems: 'center', gap: 7, flexWrap: 'wrap',
      fontSize: 11.5, color: C.textMuted, lineHeight: 1.5,
    }}>
      <ClaudeMark size={12} />
      <span title={whenText ? `Сводка собрана ${whenText}` : undefined}>
        Сводку собрал Claude{model ? ` (${model})` : ''}
      </span>
      {dot}
      <span title="Полное время ожидания ответа, включая запуск CLI">
        {fmtDuration(gen.durationMs)}
      </span>
      {dot}
      <span title={`Вход ${totalIn.toLocaleString('ru-RU')} (из них из кеша ${gen.cacheReadTokens.toLocaleString('ru-RU')}), выход ${gen.outputTokens.toLocaleString('ru-RU')}`}>
        {fmtTokens(total)} {plural(total, 'токен', 'токена', 'токенов')}
      </span>
      {gen.costUsd != null && (
        <>
          {dot}
          {/* «≈» не для красоты: на подписке деньги за вызов не списываются вовсе */}
          <span title="Оценка по тарифам API. На подписке эти деньги не списываются — цифра показывает, во сколько сборка обошлась бы по счётчику">
            ≈ {fmtCost(gen.costUsd)}
          </span>
        </>
      )}
    </div>
  );
}

// Заголовок секции-области в компактном списке: эмодзи области + название + счётчик
function SectionHeader({ area, list }: { area: string; list: ChangelogItem[] }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginBottom: 6 }}>
      <span style={{ fontSize: 15 }}>{groupEmoji(list)}</span>
      <span style={{ fontSize: 13.5, fontWeight: 700, color: C.textHeading }}>{area}</span>
      <span style={{ fontSize: 11.5, color: C.textMuted }}>{list.length}</span>
    </div>
  );
}

// Бейдж значимости по оценке 1-5: ярлык + цвет (обоснование — в пузыре-реплике при наведении).
// Цвета разведены по разным семантическим токенам, чтобы статусы чётко различались:
// Хит — красный (топ), Круто — зелёный (хорошо), Заметно — синий (норм), По мелочи — серый.
export function scoreBadge(score: number): { label: string; bg: string; color: string } {
  if (score >= 5) return { label: 'Хит', bg: C.dangerBg, color: C.dangerText };
  if (score >= 4) return { label: 'Круто', bg: C.successBg, color: C.successText };
  if (score >= 3) return { label: 'Заметно', bg: C.infoBg, color: C.info };
  return { label: 'По мелочи', bg: C.bgPanel, color: C.textMuted };
}

// Маленькая иконка Клауда (favicon) — ставится внутрь бейджа значимости и в пузырь-реплику
function ClaudeMark({ size = 11 }: { size?: number }) {
  return <img src="/favicon.svg" alt="" width={size} height={size} style={{ display: 'block', flexShrink: 0 }} />;
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
                color: isSel ? C.onAccent : (has ? C.textHeading : C.textMuted),
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
// Кнопка-иконка с подсказкой в стиле приложения (как в шапке-хабе): всплывает
// своя плашка, а не системный title — он появляется с задержкой и выглядит чужеродно.
function HintButton({ hint, onClick, disabled, children }: {
  hint: string;
  onClick: () => void;
  disabled?: boolean;
  children: ReactNode;
}) {
  const m = useIsMobile();
  const [show, setShow] = useState(false);
  return (
    <button onClick={onClick} disabled={disabled} aria-label={hint}
      style={{
        position: 'relative', width: m ? 40 : 30, height: m ? 40 : 30, borderRadius: 8, border: 'none',
        background: 'none', cursor: disabled ? 'default' : 'pointer', flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textSecondary,
      }}
      onMouseEnter={e => { if (!disabled) e.currentTarget.style.background = C.bgSelected; setShow(true); }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; setShow(false); }}>
      {children}
      {show && (
        <span style={{
          position: 'absolute', top: 'calc(100% + 7px)', right: 0, zIndex: 200,
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: 8,
          boxShadow: SHADOW.dropdown, padding: '5px 10px',
          fontSize: 12, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap',
          fontFamily: FONT.sans, pointerEvents: 'none',
        }}>
          {hint}
        </span>
      )}
    </button>
  );
}

function ArrowBtn({ dir, onClick }: { dir: 'left' | 'right'; onClick: () => void }) {
  const m = useIsMobile();
  return (
    <button onClick={onClick} aria-label={dir === 'left' ? 'Предыдущий месяц' : 'Следующий месяц'} style={{
      width: m ? 40 : 26, height: m ? 40 : 26, borderRadius: 7, border: 'none', background: 'none', cursor: 'pointer',
      display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textSecondary, flexShrink: 0,
    }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}>
      <ChevronRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE}
        style={{ transform: dir === 'right' ? 'none' : 'rotate(180deg)' }} />
    </button>
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
