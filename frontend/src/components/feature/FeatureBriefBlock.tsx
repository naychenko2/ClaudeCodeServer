// Карточка «Сводка» на вкладке «Хронология» страницы фичи (волна 6 «Сводка состояния фичи»,
// шаг F5, макет docs/mockups/feature-summary-1).
//
// Блок — только композиция: слова — FeatureBriefModel (F2), тег состояния — FeatureStatusTag
// (единая точка правды тега), полоска/ось/итоги — FeatureBriefTimeline (F4), легенда —
// FeatureBriefLegend (F3). Ничего не пересчитывает: не трогает напрямую ни поля состояния,
// ни полосы стадий, ни полей таймлайна — статы отдаёт общей модели ленты целиком
// (Grep-сторож в тесте).
//
// Состояния (макет §2–3): loading — скелетон с aria-busy; brief = null при живых статах —
// «Сводка не загрузилась» + «Повторить», полоска остаётся; brief = null и статов нет —
// блока нет; иначе — карточка состояния. Подстрока «Сейчас» и строка «Сделано» — тоже
// строки модели (R-F5): блок только вызывает модель и красит mono-части моно (числа
// подстроки — FONT.mono, «Токены поимённо»). Подпись факта без значения не рисуется
// (макет: «если какого-то поля нет, строку не рисуем»); порядок секций «Сделано ·
// Дальше · Оценка» и закрытость ленты — закреплены тестом. Одно «сейчас» на ответ
// (R-F8): относительные строки считаются от него, а не от часов в каждом рендере.

import { useEffect, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { C, R, SP, FS, FONT } from '../../lib/design';
import { Button } from '../ui/Button';
import { StatusTag, featureRunStatusTag, type FeatureRunTag } from './FeatureStatusTag';
import {
  briefDoneText, briefEstimateText, briefNextText, briefNowText, briefSubText, updatedText, voiceRoleText, voiceStaleLabel, voiceWhenText,
  type PersonaNameResolver,
} from './FeatureBriefModel';
import { MiniTimeline, MiniTimelineAxis, SummaryRow } from './FeatureBriefTimeline';
import { FeatureBriefLegend } from './FeatureBriefLegend';
import { buildTimelineModel, stageLabel, type SegmentKind } from './FeatureStatsBits';
import type { FeatureBrief, FeatureBriefKind } from '../../lib/featurePage';
import type { FeatureStats } from '../../types/featureStats';

export interface FeatureBriefBlockProps {
  // Первичный запрос страницы в полёте: вместо карточки — скелетон.
  loading?: boolean;
  // brief ответа страницы; null — бэк не посчитал / ответ не несёт поля (аддитивно).
  brief: FeatureBrief | null;
  // Статистика фичи; null — не посчитана.
  stats: FeatureStats | null;
  // Момент мержа (summary.mergedAt ответа страницы) — для строки и тега closed; в brief его нет.
  mergedAt?: string | null;
  // Резолвер имени персоны (usePersonas/getPersonaById); null — часть строки с именем опускается.
  nameOf?: PersonaNameResolver;
  isMobile?: boolean;
  // «Статистика ›» → вкладка «Статистика».
  onOpenStats: () => void;
  // «Повторить» в состоянии «Сводка не загрузилась» (перезапрос страницы).
  onRetry: () => void;
  // Кнопки состояния «ждёт вас»: только на мобиле (на десктопе «Мерж…» — в шапке страницы).
  onMerge?: () => void;
  onOpenFinal?: () => void;
}

// ── Тег состояния ────────────────────────────────────────────────────────────────────
// Тег карточки — существующий тег статуса фичи (макет «Токены поимённо»). У блока нет поля
// состояния рана — есть только brief.kind: соответствие обратное бэковой таблице
// FeatureBrief.KindByState (B1a), чисто показное — каждая kind получает каноническое
// состояние своей категории. Бэк сливает merged/closed в одну kind — различаем по mergedAt
// (тот же параметр, что для строки «Сейчас»). «в очереди» — новый вариант тега (макет:
// bgSelected/textSecondary), состояния для него в таблице нет.
export function briefTag(brief: FeatureBrief, mergedAt: string | null): FeatureRunTag {
  switch (brief.kind) {
    case 'running':
      return featureRunStatusTag('running', brief.now?.stage ? stageLabel(brief.now.stage) : null);
    case 'waiting_you':
      // Макет: в состоянии мержа тег карточки — «ждёт вас» (само «согласие на мерж» — в строке «Сейчас»).
      return featureRunStatusTag('waiting');
    case 'error':
      return featureRunStatusTag('stopped');
    case 'paused':
      return featureRunStatusTag('paused');
    case 'queued': {
      const q = brief.queue;
      return { label: q ? `в очереди · ${q.position} из ${q.total}` : 'в очереди', tone: 'notStarted' };
    }
    case 'draft':
      return featureRunStatusTag('draft');
    case 'idle':
      return featureRunStatusTag('idle');
    case 'closed':
      return featureRunStatusTag(mergedAt ? 'merged' : 'closed');
  }
}

// ── Глифы состояний (16 px, макет §«Глифы»: цвета — токены) ──────────────────────────
// Для kinds вне макета (draft/idle) глифа нет — строка сдвигается влево. «Ошибка» —
// тот же глиф, что у «Сводка не загрузилась» (R-F8): состояние ошибки карточки одно.

// Глиф состояния ошибки: утопленный круг с danger-подложкой (макет §3).
const ERROR_GLYPH = (
  <svg width={16} height={16} viewBox="0 0 16 16" aria-hidden>
    <circle cx={8} cy={8} r={7} fill={C.dangerBg} stroke={C.textMuted} strokeWidth={1} />
    <rect x={5.3} y={4.6} width={1.9} height={6.8} rx={0.6} fill={C.textSecondary} />
    <rect x={8.8} y={4.6} width={1.9} height={6.8} rx={0.6} fill={C.textSecondary} />
  </svg>
);

const GLYPH: Partial<Record<FeatureBriefKind, ReactNode>> = {
  error: ERROR_GLYPH,
  running: (
    <svg width={16} height={16} viewBox="0 0 16 16" aria-hidden>
      <circle cx={8} cy={8} r={6.5} fill="none" stroke={C.accent} strokeWidth={1.5} />
      <circle cx={8} cy={8} r={3.5} fill={C.accent} />
    </svg>
  ),
  waiting_you: (
    <svg width={16} height={16} viewBox="0 0 16 16" aria-hidden>
      <path d="M8 1.5 14.5 8 8 14.5 1.5 8Z" fill={C.info} />
    </svg>
  ),
  paused: (
    <svg width={16} height={16} viewBox="0 0 16 16" aria-hidden>
      <circle cx={8} cy={8} r={7} fill={C.bgInset} stroke={C.textMuted} strokeWidth={1} />
      <rect x={5.3} y={4.6} width={1.9} height={6.8} rx={0.6} fill={C.textSecondary} />
      <rect x={8.8} y={4.6} width={1.9} height={6.8} rx={0.6} fill={C.textSecondary} />
    </svg>
  ),
  queued: (
    <svg width={16} height={16} viewBox="0 0 16 16" aria-hidden>
      <circle cx={8} cy={8} r={7} fill={C.bgSelected} />
      <circle cx={4.8} cy={8} r={1.3} fill={C.textMuted} />
      <circle cx={8} cy={8} r={1.3} fill={C.textSecondary} />
      <circle cx={11.2} cy={8} r={1.3} fill={C.textMuted} />
    </svg>
  ),
  closed: (
    <svg width={16} height={16} viewBox="0 0 16 16" aria-hidden>
      <circle cx={8} cy={8} r={7} fill={C.successBg} stroke={C.success} strokeWidth={1} />
      <path d="m5 8.2 2 2 4-4.4" fill="none" stroke={C.success} strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
};

// ── Общие стили (макет §«Токены поимённо») ──────────────────────────────────────────
const LABEL: CSSProperties = {
  fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: '.05em', color: C.textMuted,
};
const UPD: CSSProperties = { fontSize: FS.xs, color: C.textMuted };

// ── Числа подстроки «Сейчас» — моно (R-F8, макет §«Токены поимённо») ────────────────
// Модель отдаёт подстроку строкой; блок только красит числовые токены — текст не меняет.
// В подстроке моно меняет только гарнитуру (макет: span.mono внутри .sub), цвет и вес —
// как у строки. Токены форматтеров модели: «13:58», «12 мин», «1 ч 10 мин».
const SUB_NUM: CSSProperties = { fontFamily: FONT.mono };
const NUM_TOKEN = /\d[\d.,:]*(?:\s+(?:мин|ч|сек|дней|ночи|сутки)(?:\s*\d[\d.,:]*(?:\s+(?:мин|ч|сек|дней|ночи|сутки))?)?)?/g;

function monoNumbers(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  let m: RegExpExecArray | null;
  NUM_TOKEN.lastIndex = 0;
  while ((m = NUM_TOKEN.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    out.push(<span key={m.index} style={SUB_NUM}>{m[0]}</span>);
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

// ── Хвост карточки «Жизнь фичи»: полоска, ось, легенда, итоги + «Статистика ›» ───────
// Поля статов деструктурируются и отдаются общей модели ленты как есть — блок сам ничего
// из сырых полей не считает, включая поля `timeline` (Grep-сторож теста, R-F8). Признак
// закрытости ленты — не эвристика по `current == null`, а общая модель состояния:
// `closed` приходит из карточки (brief.kind === 'closed'); без brief — не закрыта.
function LifeSection({ stats, closed, onOpenStats, isMobile }: { stats: FeatureStats; closed: boolean; onOpenStats: () => void; isMobile: boolean }) {
  const { timeline, source, truncated } = stats;
  const model = buildTimelineModel(timeline, source, truncated, closed);
  // Виды легенды — только из MAIN-сегментов: ревью — отдельная дорожка, на полоске её нет.
  const kinds = new Set<SegmentKind>();
  for (const b of model.blocks) {
    if (b.type === 'stage') for (const s of b.segments) kinds.add(s.kind);
  }
  const hasBreak = model.blocks.some(b => b.type === 'break');
  return (
    <div data-testid="brief-life" style={{ marginTop: SP.md, paddingTop: SP.md, borderTop: `1px solid ${C.borderLight}` }}>
      <div style={{ ...LABEL, marginBottom: SP.xs }}>Жизнь фичи</div>
      {model.hasData && (
        <div role="img" aria-label="Полоска жизни фичи">
          <MiniTimeline stats={stats} />
        </div>
      )}
      <MiniTimelineAxis stats={stats} />
      <FeatureBriefLegend kinds={kinds} hasBreak={hasBreak} />
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginTop: SP.xs }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <SummaryRow stats={stats} />
        </div>
        <Button
          variant="ghost"
          size="xs"
          dataTestId="brief-go-stats"
          ariaLabel="Открыть вкладку «Статистика»"
          onClick={onOpenStats}
          style={{
            background: 'transparent', border: 'none', color: C.accent,
            fontSize: FS.xs, whiteSpace: 'nowrap', flexShrink: 0,
            minHeight: isMobile ? 40 : undefined,
          }}
        >
          Статистика ›
        </Button>
      </div>
    </div>
  );
}

// ── Голос оркестратора ───────────────────────────────────────────────────────────────
// Плашка только при note (макет §3: «голоса нет» — плашки нет вовсе). Подпись —
// «Имя · роль · когда» (F2); имя неизвестно (byUser или нет персоны) — часть с именем
// опускается, аватар не рисуем. Устаревший голос — метка рядом с подписью, текст не прячем.
function VoiceBlock({ note, kind, nameOf, nowMs }: { note: NonNullable<FeatureBrief['note']>; kind: FeatureBriefKind; nameOf?: PersonaNameResolver; nowMs: number }) {
  const name = note.byPersonaId ? nameOf?.(note.byPersonaId) ?? null : null;
  const when = voiceWhenText(note, kind, nowMs);
  const sig = [name, voiceRoleText(kind), when].filter(Boolean).join(' · ');
  const stale = voiceStaleLabel(note, kind);
  return (
    <div data-testid="brief-voice" style={{
      display: 'flex', gap: SP.sm, marginTop: SP.md, padding: `${SP.sm}px ${SP.md}px`,
      background: C.bgInset, borderRadius: R.lg, borderLeft: `3px solid ${C.accentMuted}`,
    }}>
      {name && (
        <span aria-hidden style={{
          width: 24, height: 24, borderRadius: R.full, flexShrink: 0,
          background: C.accentLight, color: C.accent, fontWeight: 700, fontSize: FS.xs,
          display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        }}>
          {name.slice(0, 1).toUpperCase()}
        </span>
      )}
      <div style={{ minWidth: 0 }}>
        <div style={{ fontSize: FS.base, color: C.textPrimary }}>{note.text}</div>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: `${SP.xs}px ${SP.sm}px`, alignItems: 'center', marginTop: 2, fontSize: FS.xs, color: C.textMuted }}>
          <span data-testid="brief-voice-by">{sig}</span>
          {stale && (
            <span data-testid="brief-voice-stale" style={{ fontSize: FS.xs, fontWeight: 600, background: C.warningBg, color: C.warningText, borderRadius: R.sm, padding: '0 6px' }}>
              {stale}
            </span>
          )}
        </div>
      </div>
    </div>
  );
}

// ── Скелетон загрузки (макет §3): C.bgSelected, R.sm, высоты 8–14; без нулей и прочерков ─
function BriefSkeleton({ isMobile }: { isMobile: boolean }) {
  const sk = (extra: CSSProperties): CSSProperties => ({ height: 10, borderRadius: R.sm, background: C.bgSelected, ...extra });
  return (
    <section
      data-testid="feature-brief-skeleton"
      aria-busy="true"
      aria-label="Сводка состояния фичи"
      style={{ background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, padding: isMobile ? SP.md : SP.lg }}
    >
      <div style={sk({ width: '40%', height: 8 })} />
      <div style={sk({ width: '80%', height: 14, marginTop: SP.sm })} />
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1.5fr 1fr', gap: `${SP.md}px ${SP.xl}px`, marginTop: SP.md, paddingTop: SP.md, borderTop: `1px solid ${C.borderLight}` }}>
        {[0, 1, 2].map(i => (
          <div key={i}>
            <div style={sk({ width: 56, height: 8 })} />
            <div style={sk({ width: '90%', marginTop: SP.xs })} />
          </div>
        ))}
      </div>
      <div style={sk({ height: 44, marginTop: SP.md, borderRadius: R.lg })} />
      <div style={{ marginTop: SP.md, paddingTop: SP.md, borderTop: `1px solid ${C.borderLight}` }}>
        <div style={sk({ height: 8, borderRadius: R.max })} />
        <div style={sk({ width: '60%', height: 8, marginTop: SP.md })} />
      </div>
    </section>
  );
}

// ── Карточка ─────────────────────────────────────────────────────────────────────────
export function FeatureBriefBlock({
  loading = false, brief, stats, mergedAt = null, nameOf, isMobile = false,
  onOpenStats, onRetry, onMerge, onOpenFinal,
}: FeatureBriefBlockProps) {
  // Одно «сейчас» на ответ (R-F8): относительные строки («ждёт N», «обновлено N назад»,
  // время голоса) считаются от одного момента, привязанного к данным, а не от часов в
  // каждом рендере: перерисовка тех же данных не сдвигает текст, новый ответ — новое «сейчас».
  // «Сейчас» — состояние, а не вызов в рендере (R-F8, react-hooks/purity): тот же
  // проверенный в проекте паттерн, что usePromptCache/useNow — начальное значение при
  // монтировании, сверка часов на смену данных — в effect.
  const [nowMs, setNowMs] = useState(() => Date.now());
  // deps — данные ответа, а не аргументы эффекта: смена данных = новое «сейчас», та же
  // ссылка на данные = тот же момент.
  // eslint-disable-next-line react-hooks/set-state-in-effect -- часы живут вне React (тот же приём, что usePromptCache:22)
  useEffect(() => { setNowMs(Date.now()); }, [brief, stats, mergedAt]);
  if (loading) return <BriefSkeleton isMobile={isMobile} />;
  // «summary и stats оба null — блока нет» (макет §3).
  if (!brief && !stats) return null;

  // Закрытость ленты — из общей модели состояния (brief.kind), не из эвристики по
  // сырому текущему полю ленты (R-F8). Без brief лента не закрыта: признака нет.
  const closed = brief?.kind === 'closed';
  const life = stats ? <LifeSection stats={stats} closed={closed} onOpenStats={onOpenStats} isMobile={isMobile} /> : null;

  // «Сводка не загрузилась» (макет §3): одна строка + «Повторить»; полоска и итоги — как раньше.
  if (!brief) {
    return (
      <section data-testid="feature-brief-block" aria-label="Сводка состояния фичи" style={{
        background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
        padding: isMobile ? SP.md : SP.lg, fontFamily: FONT.sans,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <span aria-hidden style={{ display: 'inline-flex', flexShrink: 0 }}>{ERROR_GLYPH}</span>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Сводка не загрузилась</div>
            <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>Полоска и итоги ниже — как раньше.</div>
          </div>
          <Button variant="secondary" size={isMobile ? 'md' : 'sm'} dataTestId="brief-retry" onClick={onRetry}>Повторить</Button>
        </div>
        {life}
      </section>
    );
  }

  const tag = briefTag(brief, mergedAt);
  const nowText = briefNowText(brief, { nameOf, mergedAt });
  const subText = briefSubText(brief, nowMs);
  const nextText = briefNextText(brief, nameOf);
  const est = briefEstimateText(brief);
  const isWait = brief.kind === 'waiting_you';
  // «обновлено …» — по computedAt; у закрытой — дата закрытия (макет §Поведение).
  const upd = updatedText(brief.kind === 'closed' ? (mergedAt ?? brief.computedAt) : brief.computedAt, nowMs);

  const tagBlock = (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: SP.xs, flexShrink: 0 }}>
      <StatusTag tag={tag} testId="brief-tag" />
      {upd && <span data-testid="brief-updated" style={UPD}>{upd}</span>}
    </div>
  );

  const doneParts = briefDoneText(brief);
  const mono: CSSProperties = { fontFamily: FONT.mono, fontWeight: 600, color: C.textHeading };
  const factLabel = (text: string) => <div style={LABEL}>{text}</div>;
  // Ячейка факта: «подпись | значение». minWidth: 0 у ячейки и у значения — длинное слово
  // не раздвигает колонку грида (R-F8).
  const factCell = (label: string, value: ReactNode) => (
    <div style={{ minWidth: 0, ...(isMobile ? { display: 'grid', gridTemplateColumns: '72px 1fr', gap: SP.sm, alignItems: 'baseline' } : null) }}>
      {factLabel(label)}
      <div style={{ minWidth: 0 }}>{value}</div>
    </div>
  );

  return (
    <section data-testid="feature-brief-block" aria-label="Сводка состояния фичи" style={{
      background: C.bgCard, border: `1px solid ${isWait ? C.info : C.borderLight}`, borderRadius: R.lg,
      padding: isMobile ? SP.md : SP.lg, fontFamily: FONT.sans,
    }}>
      {/* Мобильная шапка: тег и «обновлено» — отдельной строкой над «Сейчас» (макет §1). */}
      {isMobile && (
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
          <StatusTag tag={tag} testId="brief-tag" />
          {upd && <span data-testid="brief-updated" style={UPD}>{upd}</span>}
        </div>
      )}

      {/* «Сейчас»: глиф + строка модели + подстрока; на десктопе тег и «обновлено» — справа. */}
      <div style={{ display: 'flex', gap: SP.sm, alignItems: 'flex-start' }}>
        {GLYPH[brief.kind] && <span aria-hidden data-testid="brief-glyph" style={{ paddingTop: 3, display: 'inline-flex', flexShrink: 0 }}>{GLYPH[brief.kind]}</span>}
        <div style={{ flex: 1, minWidth: 0 }}>
          {/* Подпись без значения не рисуется (макет: «поля нет — строку не рисуем», R-F8). */}
          {nowText && factLabel('Сейчас')}
          {nowText && (
            <div data-testid="brief-now-line" aria-live="polite" style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading, lineHeight: 1.35 }}>
              {nowText}
            </div>
          )}
          {subText && <div data-testid="brief-now-sub" style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>{monoNumbers(subText)}</div>}
          {/* Кнопки «ждёт вас» — только на мобиле (на десктопе «Мерж…» уже в шапке страницы).
              Каждая рисуется по своему пропу независимо (R-F5 п.6): без живого чата финала
              onOpenFinal не передан — «Мерж…» остаётся, путь к мержу в этом состоянии есть. */}
          {isWait && isMobile && (onMerge || onOpenFinal) && (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, marginTop: SP.sm }}>
              {onMerge && <Button variant="primary" size="md" dataTestId="brief-merge" onClick={onMerge}>Мерж…</Button>}
              {/* «Открыть финальное ревью» — secondary (R-F8): второе действие ряда, не ghost-ссылка. */}
              {onOpenFinal && <Button variant="secondary" size="md" dataTestId="brief-open-final" onClick={onOpenFinal}>Открыть финальное ревью</Button>}
            </div>
          )}
        </div>
        {!isMobile && tagBlock}
      </div>

      {/* «Сделано · Дальше · Оценка»: три колонки на десктопе, столбик «подпись | значение» на 360. */}
      <div
        data-testid="brief-facts"
        style={{
          display: 'grid',
          gridTemplateColumns: isMobile ? '1fr' : '1fr 1.5fr 1fr',
          gap: isMobile ? SP.sm : `${SP.md}px ${SP.xl}px`,
          marginTop: SP.md, paddingTop: SP.md, borderTop: `1px solid ${C.borderLight}`,
        }}
      >
        {factCell('Сделано', (
          <div data-testid="brief-done-line" style={{ fontSize: FS.base, color: C.textPrimary }}>
            {doneParts.map((p, i) => (p.mono
              ? <b key={i} style={mono}>{p.text}</b>
              : <span key={i}>{p.text}</span>))}
          </div>
        ))}
        {/* «Дальше» без строки модели не рисуется вовсе — голый заголовок не показываем (R-F8). */}
        {nextText ? factCell('Дальше', <div data-testid="brief-next-line" style={{ fontSize: FS.base, color: C.textPrimary }}>{nextText}</div>) : null}
        {factCell(est.header, (
          <>
            {/* mainMuted (queued): строка-обещание, не значение — приглушённый (макет queue). */}
            <div data-testid="brief-estimate-main" style={{ fontSize: FS.base, color: est.mainMuted ? C.textMuted : C.textPrimary }}>{est.main}</div>
            {est.fact && <div data-testid="brief-estimate-fact" style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>{est.fact}</div>}
          </>
        ))}
      </div>

      {brief.note && <VoiceBlock note={brief.note} kind={brief.kind} nameOf={nameOf} nowMs={nowMs} />}
      {life}
    </section>
  );
}
