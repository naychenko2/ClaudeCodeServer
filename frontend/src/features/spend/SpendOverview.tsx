// Экран «Обзор» аналитики токенов: hero-сводка + график по дням (stacked по
// источникам, пунктир — граница детального окна) + карточки-топы разрезов.
// Любой клик проваливает в «Анализ» с применённым контекстом.
import type { ReactNode } from 'react';
import type { SpendCardRow, SpendOverviewResponse, SpendTurnDto } from '../../types';
import { C, FONT, R, SHADOW } from '../../lib/design';
import {
  DIM_LABELS, SPEND_SOURCES, fmtDate, fmtTok, fmtTime, nodeName, sourceColor, sourceLabel,
  type SpendDim, type SpendFilter,
} from '../../lib/spend';
import { Dot, EmptyBody, GhostBtn, nodeIcon } from './spendUi';

export interface OverviewOpenCtx {
  filter?: SpendFilter; preset?: string; pivotDim?: string; day?: string; turnId?: string;
}

// Порядок серий в стеке дня (fal токенов не даёт — в стек не входит)
const STACK_SOURCES = ['chat-turn', 'one-shot', 'free'];

function Card({ title, more, onMore, col, isMobile, children }: {
  title: string; more?: string; onMore?: () => void; col: number; isMobile: boolean; children: ReactNode;
}) {
  return (
    <div style={{
      gridColumn: isMobile ? 'auto' : `span ${col}`,
      background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
      boxShadow: SHADOW.card, overflow: 'hidden', minWidth: 0,
    }}>
      <div style={{
        display: 'flex', alignItems: 'center', gap: 8, padding: '9px 14px',
        background: C.bgInset, borderBottom: `1px solid ${C.borderLight}`,
      }}>
        <span style={{ fontFamily: FONT.serif, fontSize: 14, fontWeight: 700, color: C.textHeading }}>{title}</span>
        {more && onMore && (
          <span
            onClick={onMore}
            style={{ marginLeft: 'auto', fontSize: 11, color: C.info, cursor: 'pointer', whiteSpace: 'nowrap', fontFamily: FONT.sans }}
          >
            {more}
          </span>
        )}
      </div>
      <div style={{ padding: '10px 14px 12px' }}>{children}</div>
    </div>
  );
}

// Строка топа: ранг, иконка, имя, полоса-доля, значение
function TopRow({ rank, icon, name, meta, share, value, valueColor, barColor, onClick }: {
  rank: number; icon: ReactNode; name: string; meta?: string | null; share: number;
  value: string; valueColor: string; barColor: string; onClick: () => void;
}) {
  return (
    <div
      onClick={onClick}
      style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 4px', borderRadius: R.md, cursor: 'pointer' }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
    >
      <span style={{ width: 14, fontSize: 10, color: C.textMuted, fontFamily: FONT.mono, textAlign: 'right', flexShrink: 0 }}>{rank}</span>
      {icon}
      <span style={{ fontSize: 12, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0, fontFamily: FONT.sans }}>
        {name}
      </span>
      {meta && <span style={{ fontSize: 10, color: C.textMuted, whiteSpace: 'nowrap', fontFamily: FONT.sans }}>{meta}</span>}
      <div style={{ flex: 1, height: 6, borderRadius: 3, background: C.bgSelected, overflow: 'hidden', minWidth: 36 }}>
        <div style={{ height: '100%', borderRadius: 3, width: `${Math.round(share * 100)}%`, background: barColor }} />
      </div>
      <span style={{ fontFamily: FONT.mono, fontSize: 11, fontWeight: 600, color: valueColor, minWidth: 52, textAlign: 'right', flexShrink: 0 }}>
        {value}
      </span>
    </div>
  );
}

// Карточка-топ одного разреза; клик по строке — фильтр в анализ, «разложить →» — pivot
function topCard(opts: {
  dim: SpendDim; title: string; rows: SpendCardRow[]; col: number; isMobile: boolean;
  onOpen: (ctx: OverviewOpenCtx) => void; limit?: number;
}) {
  const rows = opts.rows.slice(0, opts.limit ?? 4);
  if (!rows.length) return null;
  const max = Math.max(1, rows[0].tokens.total, ...rows.map(r => r.tokens.total));
  return (
    <Card key={opts.dim + opts.title} title={opts.title} more="разложить →" col={opts.col} isMobile={opts.isMobile}
      onMore={() => opts.onOpen({ pivotDim: opts.dim })}>
      {rows.map((r, i) => {
        const name = nodeName(opts.dim, r.key, r.name);
        const falOnly = r.tokens.total === 0 && r.falGenerations > 0;
        const isFree = opts.dim === 'source' && r.key === 'free';
        const isFal = opts.dim === 'source' && r.key === 'fal';
        const barColor = opts.dim === 'source' ? sourceColor(r.key) : C.accent;
        return (
          <TopRow
            key={r.key || '·'}
            rank={i + 1}
            icon={nodeIcon(opts.dim, name, r.meta, opts.dim === 'source' ? sourceColor(r.key) : undefined)}
            name={opts.dim === 'source' ? sourceLabel(r.key) : name}
            meta={opts.dim === 'chat' ? (r.meta === 'task' ? 'задача' : null) : null}
            share={falOnly || isFal ? 0 : r.tokens.total / max}
            value={falOnly || isFal ? `${r.falGenerations} ген.` : fmtTok(r.tokens.total)}
            valueColor={falOnly || isFal ? C.planText : isFree ? C.successText : C.accent}
            barColor={barColor}
            onClick={() => opts.onOpen({ filter: { dim: opts.dim, val: r.key, label: opts.dim === 'source' ? sourceLabel(r.key) : name } })}
          />
        );
      })}
    </Card>
  );
}

export function SpendOverview({ data, showUsers, isMobile, filtersActive, onOpen, onClearFilters, onClose }: {
  data: SpendOverviewResponse;
  showUsers: boolean;
  isMobile: boolean;
  filtersActive: boolean;      // раздел открыт со срезом (фильтры/день) — иная трактовка пустоты
  onOpen: (ctx: OverviewOpenCtx) => void;
  onClearFilters: () => void;
  onClose: () => void;
}) {
  const s = data.totals;
  const empty = s.total === 0 && data.falGenerations === 0 && data.turns === 0;
  if (empty) {
    return filtersActive ? (
      <EmptyBody pic="🔍" title="Под этот срез ничего не попало"
        text="Такая комбинация фильтров не встречалась за период. Уберите один из фильтров."
        action={<GhostBtn onClick={onClearFilters}>Сбросить срез</GhostBtn>} />
    ) : (
      <EmptyBody pic="🪙" title="Трат ещё нет"
        text="Обзор оживёт после первого хода: сводка по проектам, моделям и источникам соберётся сама. Бесплатные модели тоже попадут сюда — зелёной серией."
        action={<GhostBtn onClick={onClose}>Открыть чаты</GhostBtn>} />
    );
  }

  // График по дням: stacked-бары источников, слева от пунктира — свёрнутые дни (агрегаты)
  const dayMax = Math.max(1, ...data.byDay.map(d => d.total));
  const hasAgg = data.byDay.some(d => d.aggregated);
  const bars: ReactNode[] = [];
  let sepDone = false;
  for (const d of data.byDay) {
    if (!sepDone && !d.aggregated && hasAgg) {
      bars.push(<div key="sep" style={{ width: 0, borderLeft: `2px dashed ${C.warning}`, alignSelf: 'stretch', margin: '0 2px' }} />);
      sepDone = true;
    }
    const clickable = !d.aggregated && d.total > 0;
    bars.push(
      <div
        key={d.date}
        title={`${fmtDate(d.date)} · ${fmtTok(d.total)}${d.falGenerations ? ` · fal ${d.falGenerations} ген.` : ''}${d.aggregated ? ' · агрегат (🔒 ходы недоступны)' : ''}`}
        onClick={clickable ? () => onOpen({ day: d.date }) : undefined}
        style={{
          flex: 1, display: 'flex', flexDirection: 'column-reverse', gap: 1, minWidth: 3,
          cursor: clickable ? 'pointer' : 'default', borderRadius: '2px 2px 0 0',
          opacity: d.aggregated ? 0.4 : 1,
        }}
      >
        {d.total === 0
          ? <i style={{ display: 'block', height: 2, background: C.track, borderRadius: 1 }} />
          : STACK_SOURCES.filter(k => (d.bySource[k] ?? 0) > 0).map(k => (
              <i key={k} style={{
                display: 'block', borderRadius: 1,
                height: Math.max(2, Math.round((d.bySource[k] ?? 0) / dayMax * 118)),
                background: sourceColor(k),
              }} />
            ))}
      </div>,
    );
  }

  const days = data.byDay;
  const avgTurn = s.total / Math.max(1, data.turns);

  const heroMini = [
    { v: String(data.turns), l: 'ходов' },
    { v: fmtTok(avgTurn), l: 'средний ход' },
    { v: `${fmtTok(s.input)} / ${fmtTok(s.output)}`, l: 'in / out' },
    { v: fmtTok(s.cacheRead), l: 'cache read' },
    { v: fmtTok(data.byDay.reduce((a, d) => a + (d.bySource['free'] ?? 0), 0)), l: 'бесплатные', color: C.successText },
    { v: String(data.falGenerations), l: 'генераций fal.ai', color: C.planText },
  ];

  // Дорогие ходы — только детальное окно; клик — паспорт хода в анализе
  const topTurns = data.topTurns.slice(0, 4);
  const turnsCard = topTurns.length > 0 && (
    <Card title="Дорогие ходы" more="в анализ →" onMore={() => onOpen({})} col={showUsers ? 6 : 12} isMobile={isMobile}>
      {topTurns.map((t: SpendTurnDto, i) => (
        <TopRow
          key={t.id}
          rank={i + 1}
          icon={null}
          name={`${fmtDate(t.timestamp.slice(0, 10))} ${fmtTime(t.timestamp)}`}
          meta={[t.chatName ?? t.taskTitle ?? t.label, t.model].filter(Boolean).join(' · ') || null}
          share={t.tokens.total / Math.max(1, topTurns[0].tokens.total)}
          value={fmtTok(t.tokens.total)}
          valueColor={C.accent}
          barColor={C.accent}
          onClick={() => onOpen({
            turnId: t.id,
            filter: t.sessionId ? { dim: 'chat', val: t.sessionId, label: t.chatName ?? t.taskTitle ?? 'чат' } : undefined,
          })}
        />
      ))}
      <div style={{ fontSize: 10, color: C.textMuted, marginTop: 6, fontFamily: FONT.sans }}>
        клик — паспорт хода в анализе · только окно {data.detailDays} дней
      </div>
    </Card>
  );

  const t = (dim: SpendDim, title: string, rows: SpendCardRow[], col: number, limit?: number) =>
    topCard({ dim, title, rows, col, isMobile, onOpen, limit });

  return (
    <div style={{
      display: 'grid', gridTemplateColumns: isMobile ? '1fr' : 'repeat(12, 1fr)',
      gap: 8, padding: isMobile ? 10 : 12,
    }}>
      {/* Hero: итог + мини-метрики + график по дням */}
      <div style={{
        gridColumn: isMobile ? 'auto' : 'span 12',
        background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, boxShadow: SHADOW.card,
      }}>
        <div style={{ padding: '14px 16px', display: 'flex', gap: isMobile ? 14 : 26, alignItems: 'flex-start', flexWrap: 'wrap' }}>
          <div>
            <span
              onClick={() => onOpen({})}
              title="Открыть в анализе"
              style={{
                fontFamily: FONT.mono, fontSize: isMobile ? 28 : 34, fontWeight: 600,
                color: C.accent, lineHeight: 1, cursor: 'pointer',
              }}
            >
              {fmtTok(s.total)}
            </span>
            <div style={{ fontSize: 11, color: C.textSecondary, marginTop: 5, fontFamily: FONT.sans }}>
              {data.allUsers ? 'токены всех пользователей' : 'мои токены'} · клик — в анализ
            </div>
            <div style={{ display: 'flex', gap: 18, marginTop: 14, flexWrap: 'wrap' }}>
              {heroMini.map(m => (
                <div key={m.l}>
                  <div style={{ fontFamily: FONT.mono, fontSize: 15, fontWeight: 600, color: m.color ?? C.textHeading }}>{m.v}</div>
                  <div style={{ fontSize: 10, color: C.textMuted, marginTop: 1, fontFamily: FONT.sans }}>{m.l}</div>
                </div>
              ))}
            </div>
          </div>
          <div style={{ flex: 1, minWidth: isMobile ? '100%' : 380 }}>
            <div style={{ display: 'flex', alignItems: 'flex-end', gap: 2, height: 130 }}>{bars}</div>
            {days.length > 0 && (
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 9, color: C.textMuted, fontFamily: FONT.mono, marginTop: 4 }}>
                <span>{fmtDate(days[0].date)}</span>
                <span>{fmtDate(days[Math.floor(days.length / 2)].date)}</span>
                <span>{fmtDate(days[days.length - 1].date)}</span>
              </div>
            )}
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', marginTop: 8, alignItems: 'center' }}>
              {STACK_SOURCES.map(k => (
                <span key={k} style={{ display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: 11, color: C.textSecondary, fontFamily: FONT.sans }}>
                  <Dot color={sourceColor(k)} />{SPEND_SOURCES[k].label}
                </span>
              ))}
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: 11, color: C.textSecondary, fontFamily: FONT.sans }}>
                <Dot color={sourceColor('fal')} />fal.ai — {data.falGenerations} ген.
              </span>
              {!isMobile && (
                <span style={{ marginLeft: 'auto', fontSize: 11, color: C.textMuted, fontFamily: FONT.sans }}>
                  клик по дню → анализ дня{hasAgg ? ' · слева от пунктира — агрегаты' : ''}
                </span>
              )}
            </div>
          </div>
        </div>
      </div>

      {showUsers && t('user', DIM_LABELS.user + 'и', data.cards.users, 4)}
      {t('project', data.allUsers ? 'Проекты' : 'Мои проекты', data.cards.projects, 4)}
      {t('model', data.allUsers ? 'Модели' : 'Мои модели', data.cards.models, 4)}
      {!showUsers && t('source', 'Источники', data.cards.sources, 4)}
      {t('chat', data.allUsers ? 'Чаты и задачи' : 'Мои чаты и задачи', data.cards.chats, 6, 5)}
      {showUsers && t('source', 'Источники', data.cards.sources, 6)}
      {t('persona', 'Персоны', data.cards.personas.filter(p => p.key !== ''), 6)}
      {turnsCard}
    </div>
  );
}
