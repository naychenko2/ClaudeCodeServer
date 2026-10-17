import { useLayoutEffect, useRef, useState } from 'react';
import { C, FS, SP, R, FONT } from '../../../lib/design';
import { useIsMobile } from '../../../lib/breakpoints';
import { usePrefersReducedMotion } from '../../../hooks/usePrefersReducedMotion';
import { getPersonaById } from '../../../lib/personas';
import type { FeatureRun, FeatureTask } from '../../../types/featureRun';

// === Геометрия строки стадии (единый источник, чтобы рельса не рвалась) ===
// Точка стадии — 14px (SVG). Строка несёт рамку 1px + паддинг SP.xs, поэтому
// центр точки = SP.xs + 1 + 14/2 = 12px от верха строки (не зависит от высоты строки,
// т.к. грид строки — align-items:start: точка всегда у верха, а не по центру).
// Сегмент рельсы: top = низ текущей точки (SP.xs + 1 + 14 = 19), bottom = -(SP.xs + 1)
// (до верха следующей точки: её рамка 1 + паддинг SP.xs = 5). Тогда рельса непрерывна.
const DOT = 14;
const DOT_CY = SP.xs + 1 + DOT / 2;   // 12 — центр точки, от верха строки
const RAIL_TOP = SP.xs + 1 + DOT;     // 19 — низ текущей точки, от верха строки
// Мин. высота строки стадии: 34 — текущего значения нет в шкале SP (ближайшее SP.xxl=32),
// поэтому константа: 34 = DOT + 2·(SP.xs + 1) + 10 — точка + рамка/паддинг + запас под мета.
const ROW_MIN_H = 34;
// Бейджи возврата (↺N): паддинг 7px и line-height 18px — в шкалах SP/FS значений нет
// (SP.xs=4, SP.sm=8; FS.xs=11, FS.sm=12), значения макета v2 (.badge: padding 0 7px,
// line-height 18px) — константы.
const BADGE_PAD_X = 7;
const BADGE_LH = 18;
// X-центр сегмента рельсы (left + translateX(-50%)): отступ строки слева (padding-left
// SP.xs) + половина колонки точки 16px. `left` считается от padding-box (внутренний край
// рамки), поэтому рамку 1px не прибавляем: SP.xs + 16/2 = 4 + 8 = 12 — центр сегмента
// совпадает с центром кольца. Дуги эта константа не задевает (они от правого края).
const RAIL_LEFT = SP.xs + 16 / 2; // 12
// Ширина SVG-оверлея дуг возврата (справа): 46px — в шкале SP значения нет
// (SP.xxl=32, SP.xxxl=48), значение макета v2 (.arcsvg: width 46px).
// Экспортируется: viewBox-строка svg строится из неё и должна оставаться числом (не "100%").
export const ARCS_W = 46;

// Конвейер фичи (режим «Исполнение фичи») — строго по макету v2
// (docs/mockups/turn-plan-feature-mode-v2.html: conveyor() ~566, drawArcs() ~662,
// CSS .cv/.lanes/.arcsvg ~200–290).
//
// Структура (ОДНА вертикальная рельса, не две колонки):
//   • 7 стадий (рез → план → производство → ревью → проверка → финал → мерж), у каждого
//     СЕГМЕНТА рельсы — СВОЯ толщина: `min(2 + (min(passes,5) − 1)·1.5, 8)` px, где
//     `passes` — ЧИСЛО ПРОХОДОВ через стадию (не «число задач»): у «Производства» =
//     1 + возвраты (ревью/проверка/финал), у остальных — число их задач;
//   • стадии `todo` — пунктирный сегмент (2px dashed);
//   • активная стадия — фон C.accentLight + жирная подпись + пульсирующая точка;
//   • под строкой «Производство» — горизонтальные полосы бэк/фронт (одна под другой):
//     «бэк · N из M» + дорожка (заливка = доля принятых) + бейдж ↺r (hot при r ≥ 3);
//   • дуги возврата (SVG-оверлей справа 46px) — по КАЖДОЙ стадии-источнику (ревью/
//     проверка/финал), со сдвигом по x на 7px, цвет по стадии-источнику (макет v2:
//     review → textSecondary, check → info, final → accent), шеврон-стрелка в
//     «Производство», подпись ×N.
//
// Компонент «глупый»: фазы/проходы/линии/дуги считают ЧИСТЫЕ функции (тестируются
// отдельно), рендер только раскладывает. Координаты дуг — через замер строк (ref +
// ResizeObserver), а не фиксированный ROW_H.

type StageKey = 'cut' | 'plan' | 'prod' | 'review' | 'check' | 'final' | 'merge';
type Phase = 'done' | 'active' | 'pending';

// Стадии в порядке жизненного цикла.
const STAGES: { key: StageKey; label: string }[] = [
  { key: 'cut',    label: 'Рез' },
  { key: 'plan',   label: 'План' },
  { key: 'prod',   label: 'Производство' },
  { key: 'review', label: 'Ревью' },
  { key: 'check',  label: 'Проверка' },
  { key: 'final',  label: 'Финал' },
  { key: 'merge',  label: 'Мерж' },
];

// === Чистая логика (тестируется в node, без DOM) ===

// Все задачи роли завершены (done)? Пустой набор — false (стадия ещё не начата).
function allDone(list: FeatureTask[]): boolean {
  return list.length > 0 && list.every(t => t.status === 'done');
}

// Фазы 7 стадий по агрегату `run` (логика прежней PipelineStages, перенесена сюда).
// Пройденные — done, первая непройденная — active (текущая стадия), остальные — pending.
export function stagePhases(run: FeatureRun): Phase[] {
  const done: boolean[] = [
    !!run.epic,                                                        // Рез: эпик есть
    run.steps.length > 0,                                            // План: разложен в шаги
    allDone(run.steps),                                              // Производство: все шаги завершены
    allDone(run.reviews),                                            // Ревью: все ревью закрыты
    allDone(run.qa),                                                 // Проверка: все проверки закрыты
    run.final != null && run.final.status === 'done',               // Финал: закрыт
    (run.merge != null && run.merge.status === 'done') || run.ready, // Мерж: смержен / финал зелёный
  ];
  const firstNotDone = done.findIndex(d => !d);
  return done.map((d, i) => {
    if (firstNotDone === -1) return 'done';      // всё пройдено — активной стадии нет
    if (i === firstNotDone) return 'active';     // текущая стадия
    return d ? 'done' : 'pending';
  });
}

// Линия шага: 'back' | 'front' | null. lane в данных бывает 'back'/'front' (англ.)
// или 'бэк'/'фронт' (рус.) — нормализуем; прочие линии (тест, дизайн) не в бэк/фронт.
function laneOf(t: FeatureTask): 'back' | 'front' | null {
  const l = (t.lane ?? '').trim().toLowerCase();
  if (l === 'back' || l === 'backend' || l === 'бэк' || l === 'бэкенд') return 'back';
  if (l === 'front' || l === 'frontend' || l === 'фронт' || l === 'фронтенд') return 'front';
  return null;
}

// Число ПРОХОДОВ через стадию (не «число задач» — ошибка 2.9).
//   «Производство» = 1 + возвраты (ревью/проверка/финал) — по verdict returned/accepted_with_remarks;
//   «Ревью»/«Проверка»/«Финал» = число их задач; остальные (рез/план/мерж) = 1.
export function stagePasses(run: FeatureRun, key: StageKey): number {
  if (key === 'prod') {
    const ret = (list: FeatureTask[]) => list.filter(t => {
      const v = t.verification?.verdict;
      return v === 'returned' || v === 'accepted_with_remarks';
    }).length;
    return 1 + ret(run.reviews) + ret(run.qa) + ret(run.final ? [run.final] : []);
  }
  if (key === 'review') return run.reviews.length;
  if (key === 'check') return run.qa.length;
  if (key === 'final') return run.final ? 1 : 0;
  return 1; // cut, plan, merge
}

// Задачи стадии — единый массив для расчёта «идёт сейчас» (inProgress) активной стадии.
// `plan` — отдельной plan-задачи в агрегате нет, за неё отвечает эпик.
export function stageTasks(run: FeatureRun, key: StageKey): FeatureTask[] {
  switch (key) {
    case 'cut':    return [run.epic];
    case 'plan':   return [run.epic];
    case 'prod':   return run.steps;
    case 'review': return run.reviews;
    case 'check':  return run.qa;
    case 'final':  return run.final ? [run.final] : [];
    case 'merge':  return run.merge ? [run.merge] : [];
  }
}

// Переключение раскрытия стадии — ОДИНОЧНОЕ (2.16.5, макет v2: S.open = S.open === v ? null : v):
// открыть одну стадию — закрыть предыдущую. Клик по уже раскрытой стадии сворачивает её (null).
// Возвращает новый ключ (иммутабельно, чтобы React видел смену состояния).
export function toggleExpanded(prev: StageKey | null, key: StageKey): StageKey | null {
  return prev === key ? null : key;
}

// Толщина сегмента рельсы: pending (todo) → 2 (пунктир), иначе формула макета.
export function stageWidth(passes: number, phase: Phase): number {
  if (phase === 'pending') return 2;
  return Math.min(2 + (Math.min(passes, 5) - 1) * 1.5, 8);
}

// Линия бэк/фронт: total (все шаги), accepted (done), returns (шаги с возвратом).
export interface LaneInfo { label: string; total: number; accepted: number; returns: number }

// Линии бэк/фронт по шагам. total=0 → строка линии не выводится (решает рендер).
export function laneInfo(run: FeatureRun): { back: LaneInfo; front: LaneInfo } {
  const build = (lane: 'back' | 'front'): LaneInfo => {
    const list = run.steps.filter(s => laneOf(s) === lane);
    const total = list.length;
    const accepted = list.filter(s => s.status === 'done').length;
    const returns = list.filter(s => {
      const v = s.verification?.verdict;
      return v === 'returned' || v === 'accepted_with_remarks';
    }).length;
    return { label: lane === 'back' ? 'бэк' : 'фронт', total, accepted, returns };
  };
  return { back: build('back'), front: build('front') };
}

// Толщина дорожки линии: min(2 + r·0.5, 8), r = число шагов с возвратом.
export function laneTrackWidth(returns: number): number {
  return Math.min(2 + returns * 0.5, 8);
}

// Стадия-источник возврата: число возвратов (verdict returned/accepted_with_remarks) +
// цвет. Цвет — по ИСТОЧНИКУ (макет v2, drawArcs: COL = { review: textSecondary,
// check: info, final: accent }), а не по исходу витка: все три стадии-источника
// должны читаться как разные.
export interface ArcSource { key: 'review' | 'check' | 'final'; count: number; color: string }

// Цвет дуги по стадии-источнику (макет v2, drawArcs: COL).
const SOURCE_TINT: Record<ArcSource['key'], string> = {
  review: C.textSecondary,
  check: C.info,
  final: C.accent,
};

export function arcSpecs(run: FeatureRun): ArcSource[] {
  const out: ArcSource[] = [];
  const add = (key: ArcSource['key'], list: FeatureTask[]) => {
    const returns = list.filter(t => {
      const v = t.verification?.verdict;
      return v === 'returned' || v === 'accepted_with_remarks';
    }).length;
    if (returns <= 0) return;
    out.push({ key, count: returns, color: SOURCE_TINT[key] });
  };
  add('review', run.reviews);
  add('check', run.qa);
  if (run.final) add('final', [run.final]);
  return out;
}

// === Геометрия дуг (чистые, тестируются) ===
// Одна дуга: от стадии-источника (y) к производству (yProd). lane — смещение по x (7px),
// i — номер дуги (сдвиг по y на 2.2px, чтобы несколько дуг не сливались).
export function arcPath(y: number, yProd: number, lane: number, i: number): string {
  const x = 8 + lane * 7;
  const yy = y - i * 2.2;
  return `M2 ${yy} H${x - 3} Q${x} ${yy} ${x} ${yy - 3} V${yProd + 3} Q${x} ${yProd} ${x - 3} ${yProd} H6`;
}

// Шеврон-стрелка «приземления» на производстве.
export function chevronPath(yProd: number): string {
  return `M8 ${yProd - 3} 4 ${yProd} 8 ${yProd + 3}`;
}

// Элемент SVG-оверлея дуг: path + chevron + подпись ×N.
export interface ArcElement { d: string; chevron: string; color: string; label: string; labelX: number; labelY: number; opacity: number }

// Дуги возврата по стадиям-источникам. sources — с измеренными y (относительно контейнера),
// yProd — y производства. Возвращает элементы (path + chevron + label ×N, cap 4 дуги).
export function buildArcs(sources: { key: string; y: number; count: number; color: string }[], yProd: number): ArcElement[] {
  const out: ArcElement[] = [];
  let lane = 0;
  for (const s of sources) {
    if (s.count <= 0) continue;
    const cap = Math.min(s.count, 4);
    for (let i = 0; i < cap; i++) {
      out.push({
        d: arcPath(s.y, yProd, lane, i),
        chevron: chevronPath(yProd),
        color: s.color,
        label: '', labelX: 0, labelY: 0,
        opacity: 1 - i * 0.15,
      });
      lane++;
    }
    out.push({
      d: '', chevron: '', color: s.color,
      label: s.count > cap ? String(s.count) : `×${s.count}`,
      labelX: 8 + lane * 7 - 3, labelY: s.y + 13,
      opacity: 1,
    });
  }
  return out;
}

// === Рендер ===

// Точка-индикатор стадии: active — акцент с пульсом, done — галочка, pending — пунктир.
// ready — «Мерж» при run.ready=true: done с зелёным акцентом (кольцо C.success,
// галочка C.successText) — финал фичи зелёный, не просто серая галочка.
function StageDot({ phase, ready = false }: { phase: Phase; ready?: boolean }) {
  if (phase === 'active') {
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" className="feature-pulse">
        <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.accent} strokeWidth="1.6" />
        <circle cx="8" cy="8" r="3.5" fill={C.accent} />
      </svg>
    );
  }
  if (phase === 'done') {
    const ring = ready ? C.success : C.textMuted;
    const check = ready ? C.successText : C.textMuted;
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
        <circle cx="8" cy="8" r="7" fill="none" stroke={ring} strokeWidth="1.4" />
        <path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={check} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    );
  }
  return (
    <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
      <circle cx="8" cy="8" r="6" fill="none" stroke={C.dashed} strokeWidth="1.4" strokeDasharray="2.5 2" />
    </svg>
  );
}

// Иконка «часы» (ожидание человека).
function ClockIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 16 16" aria-hidden="true">
      <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.info} strokeWidth="1.4" />
      <path d="M8 4.5V8l2.5 1.5" fill="none" stroke={C.info} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

// Горизонтальная полоса линии (бэк/фронт) под «Производством»:
//   «бэк · N из M» + дорожка (заливка = доля принятых) + бейдж ↺r (hot при r ≥ 3).
// Сетка auto 1fr auto (строки, не колонки — на мобиле и десктопе).
function LaneBar({ info }: { info: LaneInfo }) {
  if (info.total === 0) return null; // 0 шагов в линии → строка не выводится
  const pct = info.total ? Math.round((info.accepted / info.total) * 100) : 0;
  const trackH = laneTrackWidth(info.returns);
  const hot = info.returns >= 3;
  return (
    <div
      style={{
        display: 'grid', gridTemplateColumns: 'auto 1fr auto', gap: SP.sm,
        alignItems: 'center', fontSize: FS.sm, color: C.textSecondary,
      }}
    >
      <span style={{ whiteSpace: 'nowrap' }}>{info.label} · {info.accepted} из {info.total}</span>
      <span
        role="progressbar"
        aria-label={`${info.label}: принято ${info.accepted} из ${info.total}`}
        aria-valuenow={info.accepted}
        aria-valuemax={info.total}
        style={{
          height: trackH, minHeight: 2, background: C.progressTrack,
          borderRadius: R.max, overflow: 'hidden', display: 'block',
        }}
      >
        <span style={{ display: 'block', height: '100%', width: `${pct}%`, background: C.textMuted }} />
      </span>
      {info.returns > 0 ? (
        <span
          style={{
            display: 'inline-flex', alignItems: 'center', gap: SP.xxs, fontSize: FS.xs, fontWeight: 700,
            borderRadius: R.max, padding: `0 ${BADGE_PAD_X}px`, lineHeight: `${BADGE_LH}px`, whiteSpace: 'nowrap',
            // «Холодный» (r < 3) — textSecondary на bgSelected; «горячий» (r ≥ 3) —
            // warningText на warningBg (контраст по макету v2, .badge.hot).
            background: hot ? C.warningBg : C.bgSelected,
            color: hot ? C.warningText : C.textSecondary,
          }}
        >
          ↺{info.returns}
        </span>
      ) : (
        <span />
      )}
    </div>
  );
}

// SVG-оверлей дуг возврата (справа, 46px). elements — из buildArcs (по измеренным y).
// height — реальная высота контейнера в px: viewBox 1:1 с пикселем (preserveAspectRatio="none"),
// чтобы дуги совпадали со строками.
function ArcsOverlay({ elements, height }: { elements: ArcElement[]; height: number }) {
  if (!elements.length) return null;
  return (
    <svg
      width={ARCS_W}
      height="100%"
      viewBox={`0 0 ${ARCS_W} ${Math.max(height, 1)}`}
      preserveAspectRatio="none"
      aria-hidden="true"
      style={{ position: 'absolute', top: 0, right: 0, pointerEvents: 'none', overflow: 'visible' }}
    >
      {elements.map((el, i) => (
        <g key={i} opacity={el.opacity}>
          {el.d && <path d={el.d} fill="none" stroke={el.color} strokeWidth={1.5} />}
          {el.chevron && <path d={el.chevron} fill="none" stroke={el.color} strokeWidth={1.5} strokeLinecap="round" />}
          {el.label && (
            <text
              x={el.labelX}
              y={el.labelY}
              textAnchor="end"
              style={{ font: `700 10px ${FONT.mono}`, fill: el.color }}
            >
              {el.label}
            </text>
          )}
        </g>
      ))}
    </svg>
  );
}

// === Раскрытие стадии: список шагов «Производства» (2.15.2) ===

// ISO → «ЧЧ:ММ» (отметка «с этого момента ждёт человека»). Невалидная/пустая дата → ''.
function formatWhen(iso?: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// Имя статуса (fallback, когда personaId нет): в каком состоянии задача.
function statusLabel(status?: FeatureTask['status']): string {
  switch (status) {
    case 'done': return 'закрыто';
    case 'inProgress': return 'в работе';
    case 'todo': return 'ожидает';
    default: return '—';
  }
}

// Исполнитель: имя персоны из стора персон (lib/personas), или null, если персона не найдена
// (вызывающий тогда падает на имя статуса, а не на сырой id — блокер 2.16.2).
function personaName(id?: string | null): string | null {
  if (!id) return null;
  return getPersonaById(id)?.name ?? null;
}

// Шеврон раскрытия: поворачивается на 180° при раскрытии. reduced-motion — без плавного перехода.
function Chevron({ expanded, reduced }: { expanded: boolean; reduced: boolean }) {
  return (
    <span
      aria-hidden="true"
      style={{
        display: 'inline-flex',
        transform: expanded ? 'rotate(180deg)' : 'none',
        transition: reduced ? 'none' : 'transform 0.15s ease',
        color: C.textMuted,
      }}
    >
      <svg width="12" height="12" viewBox="0 0 16 16">
        <path d="M4 6l4 4 4-4" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </span>
  );
}

// Глиф статуса шага (макет v2 .step: grid 14px 1fr auto, глиф слева, 2.16.3):
//   todo — приглушённый кружок (пунктирная рамка), inProgress — ▶ (accent), done — галочка (muted).
function StepGlyph({ status }: { status?: FeatureTask['status'] }) {
  if (status === 'inProgress') {
    return (
      <span aria-hidden="true" style={{ width: 14, display: 'inline-flex', justifyContent: 'center', color: C.accent, fontSize: FS.xs, lineHeight: 1 }}>▶</span>
    );
  }
  if (status === 'done') {
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
        <path d="M3.5 8.5 6.5 11.5 12.5 5" fill="none" stroke={C.textMuted} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    );
  }
  // todo (и дефолт, когда статуса нет): приглушённый кружок с пунктирной рамкой.
  return (
    <span aria-hidden="true" style={{ width: 14, height: 14, borderRadius: '50%', border: `1.5px dashed ${C.dashed}`, display: 'inline-block', flexShrink: 0 }} />
  );
}

// Строка шага в раскрытии «Производства»: глиф статуса, линия, stepRef, заголовок, исполнитель, ожидание человека.
function StepRow({ t }: { t: FeatureTask }) {
  const lane = laneOf(t);
  const laneLabel = lane === 'back' ? 'бэк' : lane === 'front' ? 'фронт' : (t.lane?.trim() || '—');
  // Исполнитель: имя персоны (lib/personas); если персона не найдена — имя статуса, не сырой id (2.16.2).
  const who = personaName(t.personaId) ?? statusLabel(t.status);
  const waiting = !!t.awaitingHumanSince;
  const when = formatWhen(t.awaitingHumanSince);
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '14px 1fr', gap: SP.xs, alignItems: 'start', fontSize: FS.sm }}>
      <StepGlyph status={t.status} />
      <div style={{ display: 'flex', flexDirection: 'column', gap: 1, minWidth: 0 }}>
        <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, flexWrap: 'wrap' }}>
          <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{t.stepRef ?? '—'}</span>
          <span style={{ color: C.textPrimary }}>{t.title || 'Без заголовка'}</span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.xs, color: C.textMuted }}>
          <span>{laneLabel}</span>
          <span>·</span>
          <span>{who}</span>
          {waiting && (
            <>
              <span>·</span>
              <span style={{ color: C.info }} title={t.awaitingHumanReason ?? undefined}>
                {t.awaitingHumanReason ?? 'ждёт человека'}{when ? ` · с ${when}` : ''}
              </span>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

// Раскрытие стадии: список шагов «Производства», сгруппированный по линии (бэк/фронт/прочее).
// `stageKey` — ключ стадии: детали рендерятся ТОЛЬКО для «Производства» (2.16.1), для остальных — null.
// (Имя не `key`: `key` — спец-проп React для реконсиляции, в props компонента не попадает.)
// `id` — цель aria-controls кнопки-строки. Пустой набор шагов → null (раскрытия нет).
// Поверхность (2.16.4, макет .det): фон C.bgCard, рамка C.borderLight, радиус R.md, паддинг SP.sm.
function StageDetails({ id, stageKey, run }: { id: string; stageKey: StageKey; run: FeatureRun }) {
  if (stageKey !== 'prod') return null;
  const steps = run.steps;
  if (!steps.length) return null;
  const back = steps.filter(s => laneOf(s) === 'back');
  const front = steps.filter(s => laneOf(s) === 'front');
  const other = steps.filter(s => laneOf(s) === null);
  const groups: { label: string; list: FeatureTask[] }[] = [];
  if (back.length) groups.push({ label: 'бэк', list: back });
  if (front.length) groups.push({ label: 'фронт', list: front });
  if (other.length) groups.push({ label: 'прочее', list: other });
  // Отступ слева: колонка точки (16px, см. gridTemplateColumns) + gap (SP.sm) —
  // детали выровнены по колонке контента, а не по точке.
  const indent = 16 + SP.sm;
  return (
    <div
      id={id}
      className="stage-details-bgCard"
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.xs,
        background: C.bgCard,
        border: `1px solid ${C.borderLight}`,
        borderRadius: R.md,
        padding: `${SP.sm}px ${SP.sm}px ${SP.sm}px ${indent}px`,
      }}
    >
      {groups.map(g => (
        <div key={g.label} style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          <div style={{ fontSize: FS.xs, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.04em' }}>
            {g.label}
          </div>
          {g.list.map(s => <StepRow key={s.id} t={s} />)}
        </div>
      ))}
    </div>
  );
}

// Есть ли у стадии раскрытие (детали): только «Производство» с хотя бы одним шагом (2.16.1).
// Для aria-expanded: стадия без деталей не считается «раскрытой», даже если она — текущая expanded.
function stageHasDetails(key: StageKey, run: FeatureRun): boolean {
  return key === 'prod' && run.steps.length > 0;
}

// Главная диаграмма конвейера: одна вертикальная рельса 7 стадий + полосы бэк/фронт + дуги.
export function PipelineFlow({ run }: { run: FeatureRun }) {
  const isMobile = useIsMobile();
  const reduced = usePrefersReducedMotion();
  const phases = stagePhases(run);
  const lanes = laneInfo(run);
  const specs = arcSpecs(run);
  // Раскрытая стадия (2.16.5) — ОДИНОЧНАЯ: «Производство» открыто по умолчанию, остальные свёрнуты.
  // В макете v2 по умолчанию S.open=null, но prod раскрывается при переходе на карточку
  // (a==='tocard' → S.open='prod'); делаем prod открытым сразу, чтобы шаги были видны
  // без лишнего клика (главная претензия v4: «не вижу шаги»). Клик переключает:
  // открыть одну стадию — закрыть предыдущую (toggleExpanded).
  const [expanded, setExpanded] = useState<StageKey | null>('prod');

  // Координаты строк (относительно контейнера) — через ref + ResizeObserver, не ROW_H.
  const containerRef = useRef<HTMLDivElement>(null);
  const [rowY, setRowY] = useState<Record<string, number>>({});
  // Высота контейнера в px — для viewBox дуг: 1 user unit = 1 px (preserveAspectRatio="none").
  const [svgH, setSvgH] = useState(0);

  useLayoutEffect(() => {
    const el = containerRef.current;
    if (!el || typeof window === 'undefined' || typeof ResizeObserver === 'undefined') return;
    const measure = () => {
      const box = el.getBoundingClientRect();
      const next: Record<string, number> = {};
      el.querySelectorAll<HTMLElement>('[data-stage]').forEach(node => {
        const key = node.getAttribute('data-stage');
        if (!key) return;
        // +DOT_CY — смещение к центру точки стадии (12px: рамка+паддинг+14/2).
        // В макете было +14, но его точка 16px; у нас 14px → центр 12, не 14.
        next[key] = node.getBoundingClientRect().top - box.top + DOT_CY;
      });
      setRowY(prev => {
        const keys = Object.keys(next);
        if (keys.length === Object.keys(prev).length && keys.every(k => prev[k] === next[k])) return prev;
        return next;
      });
      // React bail-out: равная высота не даёт лишнего рендера.
      setSvgH(Math.round(box.height));
    };
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Дуги: только когда есть источники возврата И координаты измерены (десктоп).
  // На мобиле (нет места) — fallback-бейджи в строке «Производство» (см. StageRow).
  let arcElements: ArcElement[] = [];
  if (!isMobile && rowY.prod != null) {
    const sources = specs
      .filter(s => rowY[s.key] != null)
      .map(s => ({ key: s.key, y: rowY[s.key], count: s.count, color: s.color }));
    arcElements = buildArcs(sources, rowY.prod);
  }

  // Fallback-бейджи возврата (мобила / нет дуг): «↺N ревью/проверка/финал» в строке «Производство».
  const fallbackBadges = specs
    .map(s => ({
      n: s.count,
      label: s.key === 'review' ? 'ревью' : s.key === 'check' ? 'проверка' : 'финал',
      color: s.color,
    }));

  return (
    <div
      role="group"
      aria-label="Конвейер фичи: стадии, линии бэк/фронт, возврат"
      ref={containerRef}
      style={{ position: 'relative', paddingRight: isMobile ? 0 : ARCS_W }}
    >
      <div style={{ display: 'flex', flexDirection: 'column' }}>
        {STAGES.map((s, i) => {
          const phase = phases[i];
          const active = phase === 'active';
          const isLast = i === STAGES.length - 1;
          const passes = stagePasses(run, s.key);
          const width = stageWidth(passes, phase);

          // Состояния активной стадии: ожидание человека (wait) / идёт сейчас (inProgress).
          const waiting = active && !!run.awaitingHumanSince;
          // «Идёт сейчас» — по задачам ЭТОЙ стадии (stageTasks): prod/review/check/final/merge/cut/plan.
          const inProgress = active && stageTasks(run, s.key).some(t => t?.status === 'inProgress');
          // run.ready=true → «Мерж» завершён с зелёным акцентом (подсветка строки + точка).
          const readyMerge = s.key === 'merge' && run.ready;

          return (
            <div
              key={s.key}
              data-stage={s.key}
              aria-current={active ? 'step' : undefined}
              style={{
                position: 'relative',
                minHeight: ROW_MIN_H,
                padding: `${SP.xs}px ${SP.xs}px`,
                borderRadius: R.lg,
                // run.ready → «Мерж» завершён: зелёная подсветка строки (C.successBg).
                background: active ? C.accentLight : readyMerge ? C.successBg : 'transparent',
                // Состояние ожидания: поверх подсветки — C.wait border (C.info) + иконка «часы».
                border: waiting ? `1px solid ${C.info}` : '1px solid transparent',
              }}
            >
              {/* Сегмент рельсы: своя толщина (width) или пунктир (pending). */}
              {!isLast && (
                <span
                  aria-hidden="true"
                  style={{
                    position: 'absolute',
                    left: RAIL_LEFT,
                    // top = низ текущей точки (RAIL_TOP), bottom = до верха следующей
                    // (-(SP.xs + 1): рамка+паддинг следующей строки) — рельса непрерывна.
                    top: RAIL_TOP,
                    bottom: -(SP.xs + 1),
                    width: phase === 'pending' ? 0 : width,
                    background: phase === 'pending' ? 'none' : C.progressTrack,
                    borderLeft: phase === 'pending' ? `2px dashed ${C.dashed}` : 'none',
                    transform: 'translateX(-50%)',
                    // 2px — скругление сегмента: в шкале R значения 2 нет (R.sm=6).
                    borderRadius: 2,
                  }}
                />
              )}
              {/* Строка стадии — кнопка (2.15.1): aria-expanded/aria-controls, клик переключает
                  раскрытие. Грид 16px 1fr (точка + контент) — как была строка-грид, только
                  теперь кликабельная. padding:0, чтобы точка осталась на месте (геометрия рельсы). */}
              <button
                data-testid={`stage-row-${s.key}`}
                aria-expanded={expanded === s.key && stageHasDetails(s.key, run)}
                aria-controls={`stage-details-${s.key}`}
                onClick={() => setExpanded(prev => toggleExpanded(prev, s.key))}
                style={{
                  display: 'grid',
                  gridTemplateColumns: '16px 1fr',
                  gap: SP.sm,
                  // 'start' (не 'center'): точка всегда у верха строки, центр = DOT_CY,
                  // независимо от высоты строки (иначе в высокой «Производство» точка
                  // центрировалась вниз и рельса рвалась над ней).
                  alignItems: 'start',
                  width: '100%',
                  padding: 0,
                  border: 'none',
                  background: 'transparent',
                  cursor: 'pointer',
                  textAlign: 'left',
                  font: 'inherit',
                  color: 'inherit',
                }}
              >
                <span style={{ display: 'flex', justifyContent: 'center', position: 'relative', zIndex: 1 }}>
                  <StageDot phase={phase} ready={readyMerge} />
                </span>
              <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
                <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
                  {/* Метка «стоит сейчас»: ▶ (accent) слева от имени, когда стадия inProgress. */}
                  {inProgress && (
                    <span aria-hidden="true" style={{ color: C.accent, fontSize: FS.xs, lineHeight: 1 }}>▶</span>
                  )}
                  <span
                    style={{
                      fontSize: FS.sm,
                      fontWeight: active ? 700 : 400,
                      color: active ? C.textHeading : phase === 'done' ? C.textSecondary : C.textMuted,
                    }}
                  >
                    {s.label}
                  </span>
                  {/* Ожидание человека: иконка «часы» (встроена в стадию, не отдельный StatusAlert). */}
                  {waiting && (
                    <span title={run.awaitingHumanReason ?? 'Ждёт вашего решения'} style={{ display: 'inline-flex', alignItems: 'center' }}>
                      <ClockIcon />
                    </span>
                  )}
                  {/* Fallback-бейджи возврата (мобила): в строке «Производство».
                      «Холодные» (n < 3) — textSecondary на bgSelected; «горячие» (n ≥ 3) —
                      warningText на warningBg (контраст по макету v2, .badge.hot). */}
                  {s.key === 'prod' && isMobile && fallbackBadges.map(b => {
                    const hot = b.n >= 3;
                    return (
                      <span
                        key={b.label}
                        style={{
                          display: 'inline-flex', alignItems: 'center', gap: SP.xxs, fontSize: FS.xs, fontWeight: 700,
                          borderRadius: R.max, padding: `0 ${BADGE_PAD_X}px`, lineHeight: `${BADGE_LH}px`, whiteSpace: 'nowrap',
                          background: hot ? C.warningBg : C.bgSelected,
                          color: hot ? C.warningText : C.textSecondary,
                        }}
                      >
                        ↺{b.n} {b.label}
                      </span>
                    );
                  })}
                  {/* Шеврон раскрытия: справа в строке, поворот при раскрытии (reduced-motion — без перехода). */}
                  <span style={{ marginLeft: 'auto', display: 'inline-flex', alignItems: 'center' }}>
                    <Chevron expanded={expanded === s.key} reduced={reduced} />
                  </span>
                </span>
                {/* Мета под именем: «ещё не начат» (todo) / «стоит сейчас» (inProgress) / reason (wait). */}
                {phase === 'pending' ? (
                  <span style={{ fontSize: FS.sm, color: C.textMuted }}>ещё не начат</span>
                ) : inProgress ? (
                  <span style={{ fontSize: FS.xs, color: C.textMuted }}>стоит сейчас</span>
                ) : waiting ? (
                  <span style={{ fontSize: FS.xs, color: C.textMuted }}>{run.awaitingHumanReason ?? 'ждёт вашего решения'}</span>
                ) : null}
                {/* Полосы бэк/фронт под «Производством» (одна под другой, строки).
                    Без margin-left: в макете v2 .lanes — sibling строки (margin-left 25px =
                    16+9 до колонки текста), а здесь полосы уже ВНУТРИ колонки текста, поэтому
                    доп. сдвиг не нужен — начало полосы по имени стадии. */}
                {s.key === 'prod' && (
                  <span style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, margin: `${SP.xxs}px 0 ${SP.xs}px 0` }}>
                    <LaneBar info={lanes.back} />
                    <LaneBar info={lanes.front} />
                  </span>
                )}
              </span>
              </button>
              {/* Раскрытие: список шагов «Производства» (2.15.2/2.16.1). Только когда стадия раскрыта;
                  StageDetails сам отдаёт null для стадий без деталей (только «Производство» их имеет).
                  `key` — React-реконсиляция, `stageKey` — данные (см. StageDetails). */}
              {expanded === s.key && (
                <StageDetails key={s.key} id={`stage-details-${s.key}`} stageKey={s.key} run={run} />
              )}
            </div>
          );
        })}
      </div>

      {/* Дуги возврата: SVG-оверлей справа (десктоп, когда места хватает). */}
      {!isMobile && <ArcsOverlay elements={arcElements} height={svgH} />}
    </div>
  );
}
