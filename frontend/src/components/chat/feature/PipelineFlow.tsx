import { useLayoutEffect, useRef, useState } from 'react';
import { C, FS, SP, R, FONT } from '../../../lib/design';
import { useIsMobile } from '../../../lib/breakpoints';
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
// Мин. высота строки стадии: 34 — в шкале SP подходящего значения нет (SP.lg=16, SP.xl=24,
// SP.xxl=32), поэтому константа; 34 = 2·SP.xs + DOT + запас под мета-строку.
const ROW_MIN_H = 34;

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
function StageDot({ phase }: { phase: Phase }) {
  if (phase === 'active') {
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" className="feature-pulse">
        <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.accent} strokeWidth="1.6" />
        <circle cx="8" cy="8" r="3.5" fill={C.accent} />
      </svg>
    );
  }
  if (phase === 'done') {
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
        <circle cx="8" cy="8" r="7" fill="none" stroke={C.textMuted} strokeWidth="1.4" />
        <path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={C.textMuted} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
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
            display: 'inline-flex', alignItems: 'center', gap: 3, fontSize: FS.xs, fontWeight: 700,
            borderRadius: R.max, padding: '0 7px', lineHeight: '18px', whiteSpace: 'nowrap',
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
      width="46"
      height="100%"
      viewBox={`0 0 46 ${Math.max(height, 1)}`}
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

// Главная диаграмма конвейера: одна вертикальная рельса 7 стадий + полосы бэк/фронт + дуги.
export function PipelineFlow({ run }: { run: FeatureRun }) {
  const isMobile = useIsMobile();
  const phases = stagePhases(run);
  const lanes = laneInfo(run);
  const specs = arcSpecs(run);

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
      style={{ position: 'relative', paddingRight: isMobile ? 0 : 46 }}
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

          return (
            <div
              key={s.key}
              data-stage={s.key}
              aria-current={active ? 'step' : undefined}
              style={{
                position: 'relative',
                display: 'grid',
                gridTemplateColumns: '16px 1fr',
                gap: SP.sm,
                // 'start' (не 'center'): точка всегда у верха строки, центр = DOT_CY,
                // независимо от высоты строки (иначе в высокой «Производство» точка
                // центрировалась вниз и рельса рвалась над ней).
                alignItems: 'start',
                minHeight: ROW_MIN_H,
                padding: `${SP.xs}px ${SP.xs}px`,
                borderRadius: R.lg,
                background: active ? C.accentLight : 'transparent',
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
                    left: 7,
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
              <span style={{ display: 'flex', justifyContent: 'center', position: 'relative', zIndex: 1 }}>
                <StageDot phase={phase} />
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
                  {/* Fallback-бейджи возврата (мобила): в строке «Производство». */}
                  {s.key === 'prod' && isMobile && fallbackBadges.map(b => (
                    <span
                      key={b.label}
                      style={{
                        display: 'inline-flex', alignItems: 'center', gap: 3, fontSize: FS.xs, fontWeight: 700,
                        borderRadius: R.max, padding: '0 7px', lineHeight: '18px', whiteSpace: 'nowrap',
                        background: C.bgSelected, color: b.color,
                      }}
                    >
                      ↺{b.n} {b.label}
                    </span>
                  ))}
                </span>
                {/* Мета под именем: «ещё не начат» (todo) / «стоит сейчас» (inProgress) / reason (wait). */}
                {phase === 'pending' ? (
                  <span style={{ fontSize: FS.sm, color: C.textMuted }}>ещё не начат</span>
                ) : inProgress ? (
                  <span style={{ fontSize: FS.xs, color: C.textMuted }}>стоит сейчас</span>
                ) : waiting ? (
                  <span style={{ fontSize: FS.xs, color: C.textMuted }}>{run.awaitingHumanReason ?? 'ждёт вашего решения'}</span>
                ) : null}
                {/* Полосы бэк/фронт под «Производством» (одна под другой, строки). */}
                {s.key === 'prod' && (
                  <span style={{ display: 'flex', flexDirection: 'column', gap: 5, margin: '2px 0 4px 25px' }}>
                    <LaneBar info={lanes.back} />
                    <LaneBar info={lanes.front} />
                  </span>
                )}
              </span>
            </div>
          );
        })}
      </div>

      {/* Дуги возврата: SVG-оверлей справа (десктоп, когда места хватает). */}
      {!isMobile && <ArcsOverlay elements={arcElements} height={svgH} />}
    </div>
  );
}
