// Карточка «Сводка» на вкладке «Хронология» страницы фичи (волна 6 «Сводка состояния фичи»,
// макет docs/mockups/feature-summary-2: пара «Готовность | Срок» вместо «Сделано · Дальше · Оценка»).
//
// Блок — только композиция: слова — FeatureBriefModel, тег состояния — FeatureStatusTag
// (единая точка правды тега), строка итогов — FeatureBriefTimeline. Ничего не пересчитывает:
// не трогает напрямую ни поля состояния, ни полосы стадий, ни полей таймлайна (Grep-сторож в
// тесте); «расходятся» и время оценки оркестратора приходят с бэка.
//
// Состояния: loading — скелетон с aria-busy; brief = null при живых статах — «Сводка не
// загрузилась» + «Повторить» и строка итогов; brief = null и статов нет — блока нет; иначе —
// карточка состояния. Подстрока «Сейчас» — строка модели, блок красит числа моно. Подпись без
// значения не рисуется. Одно «сейчас» на ответ (R-F8): относительные строки считаются от него.

import { useEffect, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { C, R, SP, FS, FONT } from '../../lib/design';
import { Button } from '../ui/Button';
import { StatusTag, featureRunStatusTag, type FeatureRunTag } from './FeatureStatusTag';
import {
  briefActiveTagLabel, briefActiveView, briefElapsedFact, briefEstimateText, briefNowText, briefReadinessText, briefSubText, briefUpdatedText, voiceRoleText, voiceStaleLabel, voiceWhenText,
  type BriefDonePart, type BriefEstimateView, type BriefReadinessView, type PersonaNameResolver,
} from './FeatureBriefModel';
import { ActiveList } from './FeatureBriefActive';
import { SummaryRow } from './FeatureBriefTimeline';
import { stageLabel } from './FeatureStatsBits';
import type { FeatureBrief, FeatureBriefKind } from '../../lib/featurePage';
import type { FeatureStats } from '../../types/featureStats';

export interface FeatureBriefBlockProps {
  // Первичный запрос страницы в полёте: вместо карточки — скелетон.
  loading?: boolean;
  // brief ответа страницы; null — бэк не посчитал / ответ не несёт поля (аддитивно).
  brief: FeatureBrief | null;
  // Статистика фичи; null — не посчитана.
  stats: FeatureStats | null;
  // Момент мержа (summary.mergedAt ответа страницы) — для строки и тега closed/merged; в brief его нет.
  mergedAt?: string | null;
  // Сколько шагов уже в master (F1: run.stepProgress.inMaster ответа страницы; в brief его
  // нет) — пометка на полоске готовности и в факте трекера. null/0 — отметки нет.
  inMaster?: number | null;
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
  // Строки «Сейчас»: id задачи → переход в задачу; «Ответить» → чат задачи; хвост → вкладка «Задачи».
  onOpenTask?: (taskId: string) => void;
  onAnswer?: (taskId: string) => void;
  onOpenTasks?: () => void;
}

// ── Тег состояния ────────────────────────────────────────────────────────────────────
// Тег карточки — существующий тег статуса фичи (макет «Токены поимённо»). У блока нет поля
// состояния рана — есть только brief.kind: соответствие обратное бэковой таблице
// FeatureBrief.KindByState (B1a), чисто показное — каждая kind получает каноническое
// состояние своей категории. 'merged' и 'closed' дают один и тот же тег: «смержена» при
// дате мержа, иначе «закрыта» (тот же параметр, что для строки «Сейчас»; Ф-1.1 —
// 'merged' для карточки ведётся ровно как 'closed'). «в очереди» — новый вариант тега
// (макет: bgSelected/textSecondary), состояния для него в таблице нет.
export function briefTag(brief: FeatureBrief, mergedAt: string | null): FeatureRunTag {
  // Несколько задач — тег считает их: «идёт · 3 в работе», «ждёт вас · 1 из 3».
  const counted = briefActiveTagLabel(brief);
  if (counted) return { ...briefTagBase(brief, mergedAt), label: counted };
  return briefTagBase(brief, mergedAt);
}

function briefTagBase(brief: FeatureBrief, mergedAt: string | null): FeatureRunTag {
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
    case 'merged':
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

// Глиф завершенности (макет closed): общая точка для 'closed' и 'merged'.
const CLOSED_GLYPH = (
  <svg width={16} height={16} viewBox="0 0 16 16" aria-hidden>
    <circle cx={8} cy={8} r={7} fill={C.successBg} stroke={C.success} strokeWidth={1} />
    <path d="m5 8.2 2 2 4-4.4" fill="none" stroke={C.success} strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" />
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
  // 'closed' и 'merged' — один глиф (Ф-1.1): для карточки это одно состояние завершенности.
  closed: CLOSED_GLYPH,
  merged: CLOSED_GLYPH,
};

// ── Общие стили (макет §«Токены поимённо») ──────────────────────────────────────────
const LABEL: CSSProperties = {
  fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: '.05em', color: C.textMuted,
};
const UPD: CSSProperties = { fontSize: FS.xs, color: C.textMuted };
// Метка предупреждения: «дольше, чем по истории», «до смены этапа», «до паузы».
const STALE_TAG: CSSProperties = { fontSize: FS.xs, fontWeight: 600, background: C.warningBg, color: C.warningText, borderRadius: R.sm, padding: '0 6px' };

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

// ── Строка итогов + «Статистика ›» ───────────────────────────────────────────────────
// Из «Жизни фичи» в сводке остаётся только она (деньги и время видны без перехода); полоска,
// ось и легенда — на вкладке «Статистика». Поля статов отдаются общей модели строки как есть.
function TotalsRow({ stats, lead, onOpenStats, isMobile }: { stats: FeatureStats; lead: { label: string; value: string } | null; onOpenStats: () => void; isMobile: boolean }) {
  return (
    <div data-testid="brief-totals" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginTop: SP.md, paddingTop: SP.sm, borderTop: `1px solid ${C.borderLight}` }}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <SummaryRow stats={stats} lead={lead} />
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
  );
}

// ── Готовность: подпись, полоска, факт трекера, чипы «ещё не начато» ─────────────────
const MONO_STRONG: CSSProperties = { fontFamily: FONT.mono, fontWeight: 600 };

function parts(list: BriefDonePart[], monoColor: string): ReactNode[] {
  return list.map((p, i) => (p.mono ? <b key={i} style={{ ...MONO_STRONG, color: monoColor }}>{p.text}</b> : <span key={i}>{p.text}</span>));
}

function ReadinessColumn({ view }: { view: BriefReadinessView }) {
  // Заливка: «готово» — accent, у закрытой — success, у устаревшей — тусклая; штриховка «в работе»
  // той же краской (макет «Токены поимённо»). Анимации заполнения нет намеренно.
  const fill = view.closed ? C.success : view.stale ? C.accentMuted : C.accent;
  const hatch = view.stale
    ? `repeating-linear-gradient(135deg, ${C.accentMuted} 0 3px, ${C.progressTrack} 3px 6px)`
    : `repeating-linear-gradient(135deg, ${C.accent} 0 3px, ${C.accentMuted} 3px 6px)`;
  // Сегмент «в master» (F1): начало залитой части — success («влито в master»), остаток
  // готового — обычная краска заливки. Только когда полоска по шкале трекера (модель
  // отдаёт 0 при оценке: на шкале оценки счётчик шагов не стоит). При закрытой фиче
  // заливка и так success — сегмент не меняет вид, меняет только смысл в aria.
  const master = view.inMasterPercent > 0 ? Math.min(view.inMasterPercent, view.percent) : 0;
  return (
    <div data-testid="brief-ready" style={{ minWidth: 0 }}>
      <div style={LABEL}>{view.label}</div>
      <div data-testid="brief-ready-caption" style={{ fontSize: FS.sm, color: C.textSecondary, margin: `${SP.xs}px 0` }}>
        {/* Число — mono 600 textHeading, у устаревшей оценки — textSecondary. */}
        {parts(view.caption, view.stale ? C.textSecondary : C.textHeading)}
      </div>
      <div
        data-testid="brief-ready-bar"
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={view.percent}
        aria-label={view.ariaLabel}
        style={{ display: 'flex', height: 8, borderRadius: R.max, overflow: 'hidden', background: C.progressTrack }}
      >
        {master > 0 && <div data-testid="brief-ready-fill-master" style={{ width: `${master}%`, background: C.success }} />}
        <div data-testid="brief-ready-fill" style={{ width: `${Math.max(0, view.percent - master)}%`, background: fill }} />
        {view.inProgressPercent > 0 && <div data-testid="brief-ready-hatch" style={{ width: `${view.inProgressPercent}%`, background: hatch }} />}
      </div>
      <div data-testid="brief-ready-tracker" style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs }}>
        {view.staleLabel && <span data-testid="brief-ready-stale" style={{ ...STALE_TAG, marginRight: SP.xs }}>{view.staleLabel}</span>}
        {parts(view.tracker, C.textPrimary)}
      </div>
      {(view.chips.length > 0 || view.chipsEmpty) && (
        <div data-testid="brief-ready-left" style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.xs, marginTop: SP.sm }}>
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>{view.chipsLabel}</span>
          {view.chips.map((c, i) => (
            <span key={i} data-testid="brief-chip" style={{
              fontSize: FS.xs, borderRadius: R.sm, padding: '1px 7px', maxWidth: '100%',
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              background: c.tone === 'next' ? C.accentLight : c.tone === 'you' ? C.infoBg : C.bgSelected,
              color: c.tone === 'next' ? C.textHeading : c.tone === 'you' ? C.textPrimary : C.textSecondary,
              fontWeight: c.tone === 'plain' ? 400 : 600,
            }}>{c.text}</span>
          ))}
          {view.chipsEmpty && <span data-testid="brief-chip-empty" style={{ fontSize: FS.sm, color: C.successText }}>{view.chipsEmpty}</span>}
        </div>
      )}
    </div>
  );
}

// ── Срок: главное число, источник, вторая оценка, «почему» ────────────────────────────
function EtaColumn({ view }: { view: BriefEstimateView }) {
  return (
    <div data-testid="brief-eta" style={{ minWidth: 0 }}>
      <div style={LABEL}>{view.header}</div>
      <div data-testid="brief-estimate-main" style={{
        marginTop: SP.xs, fontSize: view.mainMuted ? FS.base : FS.lg, fontWeight: view.mainMuted ? 400 : 600,
        color: view.mainMuted ? C.textMuted : C.textHeading, lineHeight: 1.35,
      }}>
        {monoNumbers(view.main)}
        {view.tail && <span data-testid="brief-estimate-tail" style={{ fontSize: FS.sm, fontWeight: 400, color: C.textSecondary }}> {monoNumbers(view.tail)}</span>}
      </div>
      {view.source && <div data-testid="brief-estimate-source" style={UPD}>{monoNumbers(view.source)}</div>}
      {view.alt && (
        <div data-testid="brief-estimate-alt" style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs + 2 }}>
          {monoNumbers(view.alt)}
          {view.verdict && (
            <>
              {' · '}
              {view.verdict.tone === 'ok'
                ? <span data-testid="brief-estimate-verdict" style={{ fontWeight: 600, color: C.successText }}>{view.verdict.text}</span>
                : <span data-testid="brief-estimate-verdict" style={STALE_TAG}>{view.verdict.text}</span>}
            </>
          )}
        </div>
      )}
      {view.why && (
        <div data-testid="brief-estimate-why" style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs, paddingLeft: SP.sm, borderLeft: `2px solid ${C.warning}` }}>
          {view.why}
        </div>
      )}
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
            <span data-testid="brief-voice-stale" style={STALE_TAG}>
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
      <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1.4fr 1fr', gap: `${SP.md}px ${SP.xl}px`, marginTop: SP.md, paddingTop: SP.md, borderTop: `1px solid ${C.borderLight}` }}>
        <div>
          <div style={sk({ width: 72, height: 8 })} />
          <div style={sk({ height: 8, borderRadius: R.max, marginTop: SP.sm })} />
          <div style={sk({ width: '70%', marginTop: SP.sm })} />
        </div>
        <div>
          <div style={sk({ width: 40, height: 8 })} />
          <div style={sk({ width: '60%', height: 14, marginTop: SP.sm })} />
          <div style={sk({ width: '80%', marginTop: SP.sm })} />
        </div>
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
  loading = false, brief, stats, mergedAt = null, inMaster = null, nameOf, isMobile = false,
  onOpenStats, onRetry, onMerge, onOpenFinal, onOpenTask, onAnswer, onOpenTasks,
}: FeatureBriefBlockProps) {
  // Одно «сейчас» на ответ (R-F8): относительные строки («ждёт N», «обновлено N назад»,
  // время голоса) считаются от одного момента, привязанного к данным, а не от часов в
  // каждом рендере: перерисовка тех же данных не сдвигает текст, новый ответ — новое «сейчас».
  // «Сейчас» — состояние, а не вызов в рендере (R-F8, react-hooks/purity): паттерн взят из
  // usePromptCache.ts:19-23 — начальное значение при монтировании, сверка часов на смену
  // данных — в effect. В отличие от useNow (живое обновление по таймеру) и циферблата
  // usePromptCache со своим таймером: здесь таймера нет намеренно — одно «сейчас» на ответ.
  const [nowMs, setNowMs] = useState(() => Date.now());
  // deps — данные ответа, а не аргументы эффекта: смена данных = новое «сейчас», та же
  // ссылка на данные = тот же момент.
  // eslint-disable-next-line react-hooks/set-state-in-effect -- часы живут вне React (тот же приём, что usePromptCache.ts:22)
  useEffect(() => { setNowMs(Date.now()); }, [brief, stats, mergedAt]);
  if (loading) return <BriefSkeleton isMobile={isMobile} />;
  // «summary и stats оба null — блока нет» (макет §3).
  if (!brief && !stats) return null;

  const totals = stats ? <TotalsRow stats={stats} lead={brief ? briefElapsedFact(brief) : null} onOpenStats={onOpenStats} isMobile={isMobile} /> : null;

  // «Сводка не загрузилась» (макет §3): одна строка + «Повторить» и строка итогов, если пришли статы.
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
            <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>Строка итогов ниже — как раньше.</div>
          </div>
          <Button variant="secondary" size={isMobile ? 'md' : 'sm'} dataTestId="brief-retry" onClick={onRetry}>Повторить</Button>
        </div>
        {totals}
      </section>
    );
  }

  const tag = briefTag(brief, mergedAt);
  const nowText = briefNowText(brief, { nameOf, mergedAt });
  // Задачи в работе: заголовок вместо прежней строки, кнопка «Ответить» и список.
  const active = briefActiveView(brief, { nameOf, nowMs });
  const headingText = active?.heading ?? nowText;
  const subText = active?.heading != null ? active.sub : briefSubText(brief, nowMs);
  const readiness = briefReadinessText(brief, { nameOf, inMaster });
  const eta = briefEstimateText(brief, { nameOf, nowMs });
  const isWait = brief.kind === 'waiting_you';
  const answer = active?.answer && onAnswer ? active.answer : null;
  // «обновлено …» — по updatedAt (время последнего события состояния, B6), а не по computedAt
  // (дефект f2da9354); у закрытой — дата закрытия; поля нет — строки нет (решает модель).
  const upd = briefUpdatedText(brief, mergedAt, nowMs);

  const tagBlock = (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: SP.xs, flexShrink: 0 }}>
      <StatusTag tag={tag} testId="brief-tag" />
      {upd && <span data-testid="brief-updated" style={UPD}>{upd}</span>}
    </div>
  );

  return (
    <section data-testid="feature-brief-block" aria-label="Сводка состояния фичи" style={{
      background: C.bgCard, border: `1px solid ${isWait || active?.answer ? C.info : C.borderLight}`, borderRadius: R.lg,
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
          {headingText && <div style={LABEL}>Сейчас</div>}
          {headingText && (
            <div data-testid="brief-now-line" aria-live="polite" style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading, lineHeight: 1.35 }}>
              {headingText}
            </div>
          )}
          {subText && <div data-testid="brief-now-sub" style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>{monoNumbers(subText)}</div>}
          {/* «Ответить Кире»: на обоих размерах, открывает чат задачи, которая ждёт человека. */}
          {answer && (
            <div style={{ marginTop: SP.sm }}>
              <Button variant="primary" size="md" dataTestId="brief-answer" onClick={() => onAnswer?.(answer.taskId)}>{answer.label}</Button>
            </div>
          )}
          {/* Кнопки «ждёт вас» — только на мобиле (на десктопе «Мерж…» уже в шапке страницы).
              Каждая рисуется по своему пропу независимо (R-F5 п.6): без живого чата финала
              onOpenFinal не передан — «Мерж…» остаётся, путь к мержу в этом состоянии есть. */}
          {isWait && !active?.answer && isMobile && (onMerge || onOpenFinal) && (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, marginTop: SP.sm }}>
              {onMerge && <Button variant="primary" size="md" dataTestId="brief-merge" onClick={onMerge}>Мерж…</Button>}
              {/* «Открыть финальное ревью» — secondary (R-F8): второе действие ряда, не ghost-ссылка. */}
              {onOpenFinal && <Button variant="secondary" size="md" dataTestId="brief-open-final" onClick={onOpenFinal}>Открыть финальное ревью</Button>}
            </div>
          )}
        </div>
        {!isMobile && tagBlock}
      </div>

      {active && <ActiveList view={active} isMobile={isMobile} onOpenTask={onOpenTask} onOpenTasks={onOpenTasks} />}

      {/* «Готовность | Срок»: две колонки 1.4fr 1fr на десктопе, столбик на 360. */}
      <div
        data-testid="brief-facts"
        style={{
          display: 'grid',
          gridTemplateColumns: isMobile ? '1fr' : '1.4fr 1fr',
          gap: isMobile ? SP.md : `${SP.md}px ${SP.xl}px`,
          marginTop: SP.md, paddingTop: SP.md, borderTop: `1px solid ${C.borderLight}`,
        }}
      >
        <ReadinessColumn view={readiness} />
        <EtaColumn view={eta} />
      </div>

      {brief.note && <VoiceBlock note={brief.note} kind={brief.kind} nameOf={nameOf} nowMs={nowMs} />}
      {totals}
    </section>
  );
}
