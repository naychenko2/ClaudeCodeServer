import { C, FS, SP, R } from '../../../lib/design';
import { useIsMobile } from '../../../lib/breakpoints';
import { RING } from './Rounds';
import type { FeatureRun, FeatureTask } from '../../../types/featureRun';

// Вертикальная диаграмма конвейера фичи (режим «Исполнение фичи»).
//
// Заменяет горизонтальную линейку PipelineStages: конвейер развёрнут В ВЕРТИКАЛЬ —
// временная шкала сверху вниз (рез → план → производство → ревью → проверка → финал →
// мерж). Две линии — бэк и фронт: толщина (ширина) каждой = число проходов (steps в
// линии). Петля возврата (ревью/проверка → производство) — отдельная дуга, цвет по
// исходу витка (RING). На мобиле линии бэк/фронт складываются в колонку (stacked).
// Компонент «глупый»: фазы/проходы/исход считает чистые функции (тестируются отдельно),
// рендер только раскладывает.

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

// Число проходов по линиям: сколько steps в бэке и в фронте. Толщина линии = count.
export function lanePasses(run: FeatureRun): { back: number; front: number } {
  let back = 0;
  let front = 0;
  for (const s of run.steps) {
    const lane = laneOf(s);
    if (lane === 'back') back++;
    else if (lane === 'front') front++;
  }
  return { back, front };
}

// Исход петли возврата по счётчикам бэка (run.verdicts): возврат (amber) > принят с
// правками (amber, dashed) > принят (green) > нет петли. Цвет дуги — RING[verdict].stroke.
export function returnVerdict(run: FeatureRun): 'returned' | 'accepted_with_remarks' | 'accepted' | null {
  const v = run.verdicts;
  if (v.returned > 0) return 'returned';
  if (v.acceptedWithRemarks > 0) return 'accepted_with_remarks';
  if (v.accepted > 0) return 'accepted';
  return null;
}

// === Геометрия ===
// Высота строки стадии на вертикальной шкале.
const ROW_H = 34;
// Ширина (толщина) линии бэк/фронт: базовая + шаг на каждый лишний проход, с потолком.
const LANE_MIN_W = 4;
const LANE_W_STEP = 5;
const LANE_MAX_W = 30;
const LANE_H = 30;

// Толщина линии по числу проходов: 0 → нет линии, 1 → минимальная, дальше растёт.
function laneWidth(count: number): number {
  if (count <= 0) return 0;
  return Math.min(LANE_MIN_W + (count - 1) * LANE_W_STEP, LANE_MAX_W);
}

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

// Линия бэк/фронт: вертикальная полоса, ширина = число проходов. Под ней — подпись
// «бэк ×N» / «фронт ×N». count=0 → линия-плейсхолдер (тонкая, track) + «—».
function LaneBar({ label, count, color }: { label: string; count: number; color: string }) {
  const w = laneWidth(count);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.xs }}>
      <div
        role="img"
        aria-label={`${label}: ${count} ${plural(count, 'проход', 'прохода', 'проходов')}`}
        style={{
          width: w || 2,
          height: LANE_H,
          background: count ? color : C.track,
          borderRadius: R.sm,
          opacity: count ? 1 : 0.5,
        }}
      />
      <span style={{ fontSize: FS.xs, color: count ? C.textSecondary : C.textMuted, whiteSpace: 'nowrap' }}>
        {label} {count ? `×${count}` : '—'}
      </span>
    </div>
  );
}

// Дуга возврата: от ревью (строка 3) обратно к производству (строка 2), выгиб вправо.
// Цвет — по исходу витка (RING). Рисуется SVG-оверлеем справа от шкалы.
function ReturnArc({ verdict }: { verdict: 'returned' | 'accepted_with_remarks' | 'accepted' | null }) {
  if (!verdict) return null;
  const cfg = RING[verdict] ?? RING.running;
  const x = 8;                 // x-точка у кромки шкалы (внутри 48px-оверлея)
  const bulge = 30;           // выгиб вправо
  const yProd = 2 * ROW_H + ROW_H / 2;
  const yReview = 3 * ROW_H + ROW_H / 2;
  const d = `M ${x} ${yReview} C ${x + bulge} ${yReview}, ${x + bulge} ${yProd}, ${x} ${yProd}`;
  return (
    <svg
      width="48"
      height={7 * ROW_H}
      viewBox={`0 0 48 ${7 * ROW_H}`}
      aria-hidden="true"
      style={{ position: 'absolute', top: 0, right: 0, pointerEvents: 'none', overflow: 'visible' }}
    >
      <path d={d} fill="none" stroke={cfg.stroke} strokeWidth={cfg.width} strokeDasharray={cfg.dash} strokeLinecap="round" />
      {/* Точка «приземления» на производстве */}
      <circle cx={x} cy={yProd} r={2.5} fill={cfg.stroke} />
      <title>{`Возврат: ${roundVerdictLabel(verdict)}`}</title>
    </svg>
  );
}

// Подпись исхода витка (для title дуги): вернул / принял с правками / принял.
function roundVerdictLabel(v: string): string {
  switch (v) {
    case 'returned': return 'вернул';
    case 'accepted_with_remarks': return 'принял с правками';
    case 'accepted': return 'принял';
    default: return 'идёт';
  }
}

// Русские формы: проход/прохода/проходов.
function plural(n: number, one: string, few: string, many: string): string {
  const n10 = n % 10, n100 = n % 100;
  if (n10 === 1 && n100 !== 11) return one;
  if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return few;
  return many;
}

// Вертикальная диаграмма конвейера: шкала 7 стадий + линии бэк/фронт + дуга возврата.
export function PipelineFlow({ run }: { run: FeatureRun }) {
  const isMobile = useIsMobile();
  const phases = stagePhases(run);
  const passes = lanePasses(run);
  const verdict = returnVerdict(run);

  return (
    <div
      role="group"
      aria-label="Конвейер фичи: стадии, линии бэк/фронт, возврат"
      style={{ position: 'relative', paddingRight: isMobile ? 0 : 48 }}
    >
      {/* Вертикальная шкала стадий (сверху вниз) */}
      <div style={{ display: 'flex', flexDirection: 'column' }}>
        {STAGES.map((s, i) => {
          const phase = phases[i];
          const active = phase === 'active';
          const isLast = i === STAGES.length - 1;
          return (
            <div
              key={s.key}
              aria-current={active ? 'step' : undefined}
              style={{
                position: 'relative',
                display: 'grid',
                gridTemplateColumns: '16px 1fr',
                gap: SP.sm,
                alignItems: 'center',
                minHeight: ROW_H,
                padding: `${SP.xs}px ${SP.xs}px`,
                borderRadius: R.lg,
                background: active ? C.accentLight : 'transparent',
              }}
            >
              {/* Рельса: вертикальная линия от точки вниз (кроме последней стадии) */}
              {!isLast && (
                <span
                  aria-hidden="true"
                  style={{
                    position: 'absolute',
                    left: 7,
                    top: 20,
                    bottom: -SP.xs,
                    width: 2,
                    background: C.progressTrack,
                    borderRadius: 2,
                  }}
                />
              )}
              <span style={{ display: 'flex', justifyContent: 'center', position: 'relative', zIndex: 1 }}>
                <StageDot phase={phase} />
              </span>
              <span style={{ display: 'flex', alignItems: 'center', gap: SP.md, minWidth: 0 }}>
                <span
                  style={{
                    fontSize: FS.sm,
                    fontWeight: active ? 700 : 400,
                    color: active ? C.textHeading : phase === 'done' ? C.textSecondary : C.textMuted,
                  }}
                >
                  {s.label}
                </span>
                {s.key === 'prod' && (
                  // Линии бэк/фронт: толщина (ширина) = число проходов. На мобиле — в колонку.
                  <span style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', alignItems: 'flex-end', gap: SP.md }}>
                    <LaneBar label="бэк" count={passes.back} color={C.accent} />
                    <LaneBar label="фронт" count={passes.front} color={C.plan} />
                  </span>
                )}
              </span>
            </div>
          );
        })}
      </div>

      {/* Дуга возврата: оверлей справа (десктоп) */}
      {!isMobile && <ReturnArc verdict={verdict} />}
    </div>
  );
}
