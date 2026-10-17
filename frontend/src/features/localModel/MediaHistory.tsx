// «История · 7 дней» (ADR-030): заменяет «Последний час» на вкладке «Медиа».
// Шапка (счётчик + «Обновить»), пометка о видимости, фильтры (IconSegmented +
// «Фильтры» + чипы), сводка исходов, лента по дням, хвост (ещё/конец/ошибка).
//
// Блок вне stale-обёртки: история живёт, даже когда ComfyUI лежит (макет §2).
// Записи без времени (старые, без origin) — в конец своей группы, не падают.

import { useRef, useState, type CSSProperties } from 'react';
import { AudioLines, Clapperboard, Eye, Image as ImageIcon, ListFilter, RefreshCw } from 'lucide-react';
import { C, FONT, FS, ICON_SIZE, ICON_STROKE, R, SP } from 'aihome_shell/kit';
import { Button, Chip, EmptyState, IconButton, IconSegmented, WaitingIndicator } from '../../components/ui';
import { useMe } from '../../lib/defaultPersona';
import { usePrefersReducedMotion } from '../../hooks/usePrefersReducedMotion';
import type { LocalModelMedia, MediaHistoryRecord } from './api';
import { HistoryFilterMenuPopup } from './MediaHistoryFilters';
import { MediaHistoryCard, historyFileUrl } from './MediaHistoryCard';
import {
  dayGroupLabel, historyRowTime, historyRowTitle,
  historySummaryLine, historyWaitLine, historyOutcomeBadge,
} from './mediaFormat';
import { useMediaHistory, type HistoryKind } from './useMediaHistory';

// Счётчик: сумма счётчиков исходов = всего завершённых в текущей выборке
function totalOf(counters: { outcomes: Record<string, number> }): number {
  return Object.values(counters.outcomes ?? {}).reduce((a, b) => a + (b ?? 0), 0);
}

// Строка 3: «masha · очередь 1 мин 20 с»; «вы» — акцент
function ownerPart(record: MediaHistoryRecord, meUsername: string | null): { text: string; me: boolean } {
  if (record.owner == null) return { text: '—', me: false };
  const me = meUsername != null && record.owner === meUsername;
  return { text: record.owner, me };
}

// === Строка записи ===

function HistoryRow({ record, isMobile, selected, meUsername, onOpen }: {
  record: MediaHistoryRecord;
  isMobile: boolean;
  selected: boolean;
  meUsername: string | null;
  onOpen: () => void;
}) {
  const [hover, setHover] = useState(false);
  const badge = historyOutcomeBadge(record.status);
  const who = ownerPart(record, meUsername);
  const title = historyRowTitle(record.kind, record.op);
  const time = historyRowTime(record);
  const wait = historyWaitLine(record.enqueuedAt, record.startedAt);
  // миниатюра: только у завершённых картинок (у звука/видео/ошибок — плитка вида)
  const thumb = record.kind === 'image' && record.status === 'completed'
    ? historyFileUrl(record.id, 0, 'thumb')
    : null;

  const aria = `${title}, ${badge ? badge.label : 'в процессе'}, ${who.text}, ${time}. Открыть детали`;

  // Плеер прямо в ленте: у завершённого звука. preload="none" — иначе лента в 50
  // записей закачает 50 файлов. Плеер — сосед кнопки, не внутри (клик по «play»
  // не должен открывать карточку).
  const showAudio = record.kind === 'audio' && record.status === 'completed';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', width: '100%' }}>
      <button
        onClick={onOpen}
        onMouseEnter={() => setHover(true)}
        onMouseLeave={() => setHover(false)}
        aria-label={aria}
        style={{
          display: 'flex', gap: SP.sm, width: '100%', textAlign: 'left',
          padding: SP.sm, borderRadius: R.md, border: 'none', cursor: 'pointer',
          minHeight: isMobile ? 64 : 56, alignItems: 'flex-start',
          background: selected ? C.accentLight : (hover ? C.bgInset : 'transparent'),
          color: 'inherit', fontFamily: 'inherit',
        }}
      >
        {/* Миниатюра / плитка вида */}
        <span style={{
          width: 40, height: 40, flexShrink: 0, borderRadius: R.md, overflow: 'hidden',
          position: 'relative', background: C.bgInset,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          color: C.textSecondary,
        }}>
          {thumb
            ? <img src={thumb} alt="" loading="lazy" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
            : record.kind === 'audio'
              ? <AudioLines size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              : record.kind === 'video'
                ? <Clapperboard size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                : <ImageIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        </span>

        {/* Тело: 3 строки */}
        <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
          <span style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
            <b style={{
              fontSize: FS.sm, fontWeight: 600, color: C.textHeading, flex: 1, minWidth: 0,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {title}
            </b>
            {badge && (
              <span style={{
                flexShrink: 0, display: 'inline-flex', alignItems: 'center', gap: 3,
                borderRadius: R.max, padding: '2px 8px', fontSize: FS.xs, fontWeight: 700, whiteSpace: 'nowrap',
                background: badge.tone === 'ok' ? C.successBg : badge.tone === 'fail' ? C.dangerBg : badge.tone === 'int' ? C.warningBg : C.bgInset,
                color: badge.tone === 'ok' ? C.successText : badge.tone === 'fail' ? C.dangerText : badge.tone === 'int' ? C.warningText : C.textSecondary,
              }}>
                {badge.label}
              </span>
            )}
          </span>
          <span style={{
            fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.mono,
            display: 'flex', gap: SP.sm, overflow: 'hidden',
          }}>
            <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {record.model ?? '—'}
            </span>
            <span style={{ flexShrink: 0, color: C.textMuted }}>{time}</span>
          </span>
          <span style={{
            fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>
            <span style={who.me ? { color: C.textHeading, fontWeight: 600 } : { color: C.textSecondary }}>
              {who.me ? 'вы' : who.text}
            </span>
            {' · '}{wait}
          </span>
        </span>
      </button>
      {showAudio && (
        <audio
          controls
          preload="none"
          src={historyFileUrl(record.id, 0, 'full')}
          style={{ width: '100%', height: 32, marginTop: SP.xs }}
        />
      )}
    </div>
  );
}

// === Скелетон: три строки-заглушки (пульс; reduced-motion — статично) ===

function SkeletonRow() {
  const reduced = usePrefersReducedMotion();
  const pulse: CSSProperties = reduced ? {} : { animation: 'ccs-lm-skeleton 1.4s ease-in-out infinite' };
  return (
    <div style={{ display: 'flex', gap: SP.sm, padding: SP.sm }}>
      <span style={{ width: 40, height: 40, flexShrink: 0, borderRadius: R.md, background: C.bgInset, ...pulse }} />
      <span style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 6, paddingTop: 2 }}>
        <span style={{ height: 10, width: '70%', borderRadius: 6, background: C.bgInset, ...pulse }} />
        <span style={{ height: 10, width: '45%', borderRadius: 6, background: C.bgInset, ...pulse }} />
        <span style={{ height: 10, width: '60%', borderRadius: 6, background: C.bgInset, ...pulse }} />
      </span>
    </div>
  );
}

// === Чипы активных условий ===

function ActiveChips({ filters, onPatch, onReset }: {
  filters: import('./useMediaHistory').HistoryFilters;
  onPatch: (p: Partial<import('./useMediaHistory').HistoryFilters>) => void;
  onReset: () => void;
}) {
  const chips: { key: string; label: string; value: string; clear: () => void }[] = [];
  if (filters.model) chips.push({ key: 'model', label: 'Модель', value: filters.model, clear: () => onPatch({ model: null }) });
  if (filters.login) chips.push({ key: 'login', label: 'Пользователь', value: filters.login, clear: () => onPatch({ login: null }) });
  if (filters.outcome) {
    const map: Record<string, string> = { completed: 'готово', cancelled: 'отменено', failed: 'ошибка', interrupted: 'прервано' };
    chips.push({ key: 'outcome', label: 'Исход', value: map[filters.outcome] ?? filters.outcome, clear: () => onPatch({ outcome: null }) });
  }
  if (filters.period !== '7d') {
    const map: Record<string, string> = { today: 'сегодня', '24h': '24 ч' };
    chips.push({ key: 'period', label: 'Период', value: map[filters.period] ?? '', clear: () => onPatch({ period: '7d' }) });
  }
  if (chips.length === 0) return null;
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center', marginBottom: SP.sm }}>
      {chips.map(c => (
        <Chip key={c.key} onRemove={c.clear} removeLabel={`Убрать ${c.label.toLowerCase()}`}>
          <span style={{ color: C.textMuted }}>{c.label}</span>{' '}
          <span style={{ color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis' }}>{c.value}</span>
        </Chip>
      ))}
      <button
        onClick={onReset}
        style={{
          background: 'none', border: 'none', cursor: 'pointer', padding: 0, alignSelf: 'center',
          fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.sans,
          textDecoration: 'underline', textUnderlineOffset: 2,
        }}
      >
        Сбросить
      </button>
    </div>
  );
}

// === Блок ===

export function MediaHistory({ media, isMobile }: {
  media: LocalModelMedia | null;
  isMobile: boolean;
}) {
  const h = useMediaHistory(true, media);
  const me = useMe();
  const [menuRect, setMenuRect] = useState<DOMRect | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const menuBtnRef = useRef<HTMLButtonElement>(null);

  // «Активный фильтр» = отличается от дефолта (all/7d/без). Счётчик «X из Y» и
  // сводка — при любом; бейдж на кнопке «Фильтры» — кроме вида (макет §3).
  const filtered = h.filters.kind !== 'all' || h.filters.period !== '7d' || h.filters.outcome != null || h.filters.model != null || h.filters.login != null;
  const total = totalOf(h.counters);
  const counter = h.loading ? '' : (filtered ? `${h.records.length} из ${total}` : String(total));

  const summary = historySummaryLine(h.counters);
  const selected = h.records.find(r => r.id === selectedId) ?? null;
  const selIndex = selected ? h.records.indexOf(selected) : -1;

  // ←/→ по ленте: на последней загруженной «→» догружает страницу
  const goPrev = selIndex > 0 ? () => setSelectedId(h.records[selIndex - 1].id) : null;
  const goNext = selIndex >= 0 && selIndex < h.records.length - 1
    ? () => setSelectedId(h.records[selIndex + 1].id)
    : (selIndex === h.records.length - 1 && h.cursor != null ? () => h.loadMore() : null);

  // Группы по дням: newest-first, метка из finishedAt ?? startedAt ?? enqueuedAt
  const groups: { label: string; items: MediaHistoryRecord[] }[] = [];
  for (const r of h.records) {
    const t = r.finishedAt ?? r.startedAt ?? r.enqueuedAt;
    const label = t ? dayGroupLabel(t) : '';
    const last = groups[groups.length - 1];
    if (last && last.label === label) last.items.push(r);
    else groups.push({ label, items: [r] });
  }

  return (
    <section style={{
      background: C.bgWhite, border: `1px solid ${C.borderLight}`,
      borderRadius: R.xl, padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm,
    }}>
      {/* Шапка: заголовок + счётчик + «Обновить» */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: 24 }}>
        <h3 style={{
          margin: 0, flex: 1, fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase',
          letterSpacing: '0.06em', color: C.textSecondary,
        }}>
          История · 7 дней
        </h3>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{counter}</span>
        <IconButton size="xs" variant="ghost" ariaLabel="Обновить историю" onClick={h.refresh}>
          <RefreshCw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>

      {/* Пометка о видимости */}
      <div style={{
        display: 'flex', gap: 6, alignItems: 'flex-start', fontSize: FS.xs, lineHeight: 1.45,
        color: C.textSecondary, background: C.bgInset, borderRadius: R.md, padding: '4px 8px',
      }}>
        <Eye size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
        <span>Видны генерации всех пользователей AI Home — промпты и результаты. Этот раздел доступен только администраторам.</span>
      </div>

      {/* Фильтры: вид (IconSegmented) + «Фильтры» */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <IconSegmented<HistoryKind>
          value={h.filters.kind}
          onChange={v => h.setFilters({ kind: v })}
          options={[
            { value: 'all', label: 'Все виды', icon: <span style={{ fontSize: FS.xs, fontWeight: 600 }}>Все</span> },
            { value: 'image', label: 'Картинки', icon: <ImageIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
            { value: 'audio', label: 'Звук', icon: <AudioLines size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
            { value: 'video', label: 'Видео', icon: <Clapperboard size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
          ]}
        />
        <button
          ref={menuBtnRef}
          onClick={() => setMenuRect(menuBtnRef.current?.getBoundingClientRect() ?? null)}
          style={{
            marginLeft: 'auto', height: 24, display: 'inline-flex', alignItems: 'center', gap: 4,
            padding: '0 8px', borderRadius: R.md, border: `1px solid ${C.border}`,
            fontSize: FS.sm, color: C.textSecondary, background: 'transparent',
            cursor: 'pointer', fontFamily: FONT.sans,
          }}
        >
          <ListFilter size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          Фильтры
          {filtered && (
            <span style={{
              background: C.accentLight, color: C.textHeading, borderRadius: R.max,
              fontSize: FS.xs, fontWeight: 700, padding: '0 5px',
            }}>
              {countActiveChips(h.filters)}
            </span>
          )}
        </button>
      </div>

      {/* Чипы активных условий */}
      <ActiveChips filters={h.filters} onPatch={h.setFilters} onReset={h.resetFilters} />

      {/* Сводка исходов (скрыта, когда есть фильтры — её роль у чипов) */}
      {!filtered && summary && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>{summary}</div>
      )}

      {/* Тело: скелетон / ошибка / пусто / лента */}
      {h.loading ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <SkeletonRow /><SkeletonRow /><SkeletonRow />
        </div>
      ) : h.error === 'list' ? (
        <div style={{
          display: 'flex', gap: SP.sm, alignItems: 'flex-start',
          background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
          color: C.dangerText, borderRadius: R.lg, padding: '8px 12px', fontSize: FS.sm, lineHeight: 1.45,
        }}>
          <span style={{ flex: 1 }}>
            <b>Не удалось загрузить историю.</b> Сервер не ответил — живые данные выше от этого не зависят.
            <div style={{ marginTop: SP.sm }}>
              <Button variant="primary" size="sm" leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={h.retry}>
                Повторить
              </Button>
            </div>
          </span>
        </div>
      ) : h.records.length === 0 ? (
        filtered ? (
          <EmptyState
            compact
            icon={<ListFilter size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
            title="Ничего не нашлось"
            subtitle="За 7 дней нет записей с такими условиями."
            action={<Button variant="primary" size="sm" onClick={h.resetFilters}>Сбросить фильтры</Button>}
          />
        ) : (
          <EmptyState
            compact
            icon={<HistoryIcon />}
            title="Пока нет генераций"
            subtitle="Здесь появятся картинки, звук и видео, которые считались на своей видеокарте за последние 7 дней."
          />
        )
      ) : (
        <div style={{ margin: '0 -8px', display: 'flex', flexDirection: 'column' }}>
          {groups.map(g => (
            <div key={g.label || 'no-date'}>
              <div style={{
                fontSize: FS.xs, fontWeight: 700, color: C.textMuted, padding: '8px 8px 2px',
              }}>
                {g.label || 'Без даты'}
              </div>
              {g.items.map(r => (
                <HistoryRow
                  key={r.id}
                  record={r}
                  isMobile={isMobile}
                  selected={r.id === selectedId}
                  meUsername={me.username}
                  onOpen={() => setSelectedId(r.id)}
                />
              ))}
            </div>
          ))}

          {/* Хвост: показать ещё / загрузка / ошибка подгрузки / конец */}
          {h.error === 'more' ? (
            <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', gap: SP.sm, paddingTop: SP.sm }}>
              <span style={{ fontSize: FS.xs, color: C.dangerText }}>Не удалось подгрузить.</span>
              <Button variant="ghost" size="sm" leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={h.loadMore}>
                Повторить
              </Button>
            </div>
          ) : h.loadingMore ? (
            <div style={{ display: 'flex', justifyContent: 'center', paddingTop: SP.sm }}>
              <WaitingIndicator />
            </div>
          ) : h.cursor != null ? (
            <div style={{ display: 'flex', justifyContent: 'center', paddingTop: SP.sm }}>
              <Button variant="secondary" size="sm" onClick={h.loadMore}>Показать ещё</Button>
            </div>
          ) : (
            <div style={{ textAlign: 'center', fontSize: FS.xs, color: C.textMuted, padding: '8px 0 2px' }}>
              {periodTail(h.filters.period)}
            </div>
          )}
        </div>
      )}

      {/* Меню фильтров */}
      {menuRect && (
        <HistoryFilterMenuPopup
          anchor={menuRect}
          isMobile={isMobile}
          filters={h.filters}
          counters={h.counters}
          records={h.records}
          meUsername={me.username}
          onPatch={p => { h.setFilters(p); }}
          onReset={() => { h.resetFilters(); setMenuRect(null); }}
          onClose={() => setMenuRect(null)}
        />
      )}

      {/* Карточка записи */}
      {selected && (
        <MediaHistoryCard
          record={selected}
          isMobile={isMobile}
          meUsername={me.username}
          onPrev={goPrev}
          onNext={goNext}
          onClose={() => setSelectedId(null)}
        />
      )}
    </section>
  );
}

// Число активных условий (счётчик на кнопке «Фильтры»)
function countActiveChips(f: import('./useMediaHistory').HistoryFilters): number {
  let n = 0;
  if (f.model) n++;
  if (f.login) n++;
  if (f.outcome) n++;
  if (f.period !== '7d') n++;
  return n;
}

// Подпись конца ленты по периоду
function periodTail(p: import('./mediaFormat').HistoryPeriod): string {
  switch (p) {
    case 'today': return 'Это все генерации за сегодня';
    case '24h': return 'Это все генерации за 24 часа';
    case '7d': return 'Это все генерации за 7 дней';
  }
}

// Иконка «история» для пустого состояния
function HistoryIcon() {
  return (
    <svg width={ICON_SIZE.lg} height={ICON_SIZE.lg} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={ICON_STROKE} strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 12a9 9 0 1 0 9-9 9.8 9.8 0 0 0-6.7 2.7L3 8" />
      <path d="M3 3v5h5" />
      <path d="M12 7v5l4 2" />
    </svg>
  );
}
