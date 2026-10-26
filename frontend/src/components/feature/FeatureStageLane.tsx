// Дорожка этапа «Хронологии» (GC-F5a, план «Гант на Хронологии», макеты feature-gantt-6/7).
// Рендерит StageLane из FeatureLaneModel как есть: ничего НЕ считает, всё позиционирует
// через axisFrac — ту же функцию, что у модели и оси (одна шкала на все дорожки).
// Своего состояния нет: roving tabindex, раскрытие поповера и hover — у родителя
// (GC-F5c/GC-F6) и приходят пропсами.
//
// Геометрия (feature-gantt-6 §«Спецификация для фронта»): дорожка 14 px, интервал 10 px
// (top 2 px), минимальная ширина 3 px — она расширяет только нарисованное, left остаётся
// точным. Цвета по состоянию интервала: done C.success, live C.accent, wait C.info,
// stalled — штриховка C.warning/C.warningBg; пауза — пунктир 1 px C.textMuted по середине
// дорожки; lastOutcome — риска 2 px C.danger на конце интервала; возвраты — «↩» C.dangerText
// над дорожкой в момент вердикта (возвраты в модели есть только у prod).
//
// Продуктовое решение (находка m1 ревью GC-R-F2, 26.10): счётчик плана бывает только у
// «Производства». На дорожке это контур «ещё N» — он рисован ТОЛЬКО у prod; у прочих этапов,
// где работа ещё не начиналась, — слово «впереди» (слово, не число); у «Ревью» никаких
// плановых элементов нет: факт уже в aria-label каждого интервала («интервал i из n»).
//
// Состояние «ждёт» (контракт из ревью GC-R-F2R): интервал красится C.info — модель ставит
// 'wait' только интервалам, чей taskIds содержит ждущую задачу. То, что склеенный интервал
// частично занят чужой работой той же стадии, видно в деталях (GC-F6), не на дорожке.

import { C, R, FS, FONT, SP } from '../../lib/design';
import { axisFrac, type LaneAxis, type LaneIntervalState, type StageLane } from './FeatureLaneModel';
import { formatClock, formatDateTime, formatDuration } from './FeatureStatsBits';

export interface FeatureStageLaneProps {
  // Дорожка этапа из buildStageLanes.
  lane: StageLane;
  // Ось модели — одна для всех дорожек и оси вкладки.
  axis: LaneAxis;
  // Имя этапа для aria-label («{Этап}, интервал i из n: …») — его даёт вызывающий
  // (stageLabel(key)); компонент имён стадий не знает.
  label: string;
  // Индекс интервала с tabIndex=0. Roving tabindex: одна точка табуляции на весь Гант —
  // её выбирает родитель. По умолчанию 0 — первый интервал (одиночное использование).
  tabIndexAt?: number;
  // Интервал с закреплённым поповером/шторкой (aria-expanded="true"); null — ничего.
  expandedAt?: number | null;
  // Клик / Enter / Space по интервалу: родитель открывает поповер (1280) или шторку (360).
  onSelect?: (index: number) => void;
  // Наведение-превью (GC-F6); null — курсор ушёл с интервала.
  onHover?: (index: number | null) => void;
}

// Штриховка «без движения» (135°, та же геометрия штриховки, что у notWorking в FeatureStatsBits).
const STALLED_HATCH = `repeating-linear-gradient(135deg, ${C.warning} 0 5px, ${C.warningBg} 5px 10px)`;

// Заливка по состоянию интервала — цветовая таблица feature-gantt-6 §«Интервалы вместо
// одной полосы». Состояние решает модель (FeatureLaneModel), компонент только красит.
const stateBackground = (state: LaneIntervalState): string => {
  switch (state) {
    case 'done': return C.success;
    case 'live': return C.accent;
    case 'wait': return C.info;
    case 'stalled': return STALLED_HATCH;
  }
};

// Длительность интервала в минуты — та же арифметика spanMinutes, что у модели и Статистики
// (одна на весь продукт, урок W5-R-F3b). Нужна только для aria-label.
function spanMinutes(from: string, to: string): number {
  const a = new Date(from).getTime();
  const b = new Date(to).getTime();
  if (Number.isNaN(a) || Number.isNaN(b)) return 0;
  return Math.max(0, Math.round((b - a) / 60000));
}

export function FeatureStageLane({
  lane, axis, label, tabIndexAt = 0, expandedAt = null, onSelect, onHover,
}: FeatureStageLaneProps) {
  const n = lane.intervals.length;
  // Процент оси для момента: ровно axisFrac модели × 100 — тесты сверяют координаты
  // дорожки с этой же функцией.
  const pct = (iso: string): number => axisFrac(iso, axis) * 100;

  // Зона «стоит» отдельным элементом — только когда на дорожке нет штрихованного интервала:
  // это случай «начатая задача без живого хода» (макет). Когда живой интервал уже штрихован
  // моделью (state 'stalled'), зона не рисуется поверх — тот же время не дважды.
  const stalledZone = lane.stalled && !lane.intervals.some(iv => iv.state === 'stalled') ? lane.stalled : null;

  // Зона «впереди» на оси есть только у живой фичи: у закрытой timeFrac = 1, зоны нет.
  const hasAheadZone = axis.timeFrac < 1 && !lane.skipped && !lane.approx;
  // «ещё N» — только prod (продуктовое решение m1 GC-R-F2): у других стадий это число
  // значит другое, а счётчик плана — только у «Производства». Только для этапа,
  // где работа уже шла (иначе это pending — ниже «впереди»).
  const showMore = hasAheadZone && lane.key === 'prod' && n > 0 && (lane.plan?.ahead ?? 0) > 0;
  // «впереди» — этап, чья работа ещё не начиналась: ни интервалов, ни skipped, ни approx.
  const showAhead = hasAheadZone && n === 0;

  return (
    <div
      data-testid={`lane-${lane.key}`}
      data-stage={lane.key}
      style={{
        position: 'relative',
        height: 14,
        borderRadius: R.sm,
        // Тики четвертей оси (макет .gtrack) — как в прежнем Ганте.
        background: `repeating-linear-gradient(90deg, transparent 0 calc(25% - 1px), ${C.borderLight} calc(25% - 1px) 25%)`,
      }}
    >
      {lane.skipped && (
        <>
          <div
            data-testid={`lane-skipped-line-${lane.key}`}
            style={{ position: 'absolute', left: 0, right: 0, top: 7, borderTop: `1px dotted ${C.dashed}` }}
          />
          <span
            data-testid={`lane-skipped-${lane.key}`}
            style={{
              position: 'absolute', left: 0, top: 0, bottom: 0, display: 'flex', alignItems: 'center',
              paddingLeft: SP.xs, fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted,
            }}
          >
            этапа не было
          </span>
        </>
      )}

      {stalledZone && (
        <div
          data-testid={`lane-stalled-${lane.key}`}
          style={{
            position: 'absolute', left: `${pct(stalledZone.from)}%`,
            width: `${Math.max(0, pct(stalledZone.to) - pct(stalledZone.from))}%`,
            top: 2, height: 10, borderRadius: R.sm, background: STALLED_HATCH,
          }}
        />
      )}

      {lane.intervals.map((iv, i) => {
        const left = pct(iv.from);
        const width = Math.max(0, pct(iv.to) - left);
        return (
          <button
            key={`iv-${i}`}
            type="button"
            data-testid={`lane-interval-${lane.key}-${i}`}
            data-state={iv.state}
            aria-label={`${label}, интервал ${i + 1} из ${n}: ${formatDateTime(iv.from)} – ${formatClock(iv.to)}, ${formatDuration(spanMinutes(iv.from, iv.to))}`}
            aria-haspopup="dialog"
            aria-expanded={expandedAt === i}
            tabIndex={i === tabIndexAt ? 0 : -1}
            onClick={() => onSelect?.(i)}
            onMouseEnter={() => onHover?.(i)}
            onMouseLeave={() => onHover?.(null)}
            style={{
              position: 'absolute',
              left: `${left}%`,
              width: `${width}%`,
              // Минимальная видимая ширина (макет: 3 px) — этап в минуты не превращается
              // в щель; left при этом остаётся точным.
              minWidth: 3,
              top: 2,
              height: 10,
              borderRadius: R.sm,
              background: stateBackground(iv.state),
              border: 'none',
              padding: 0,
              cursor: 'pointer',
            }}
          >
            {iv.lastOutcome && (
              // Риска 2 px на конце интервала с плохим исходом последнего хода.
              <span
                data-testid={`lane-risk-${lane.key}-${i}`}
                aria-hidden="true"
                style={{ position: 'absolute', right: 0, top: 0, bottom: 0, width: 2, background: C.danger }}
              />
            )}
          </button>
        );
      })}

      {lane.intervals.slice(0, -1).map((iv, i) => {
        if (iv.pauseAfterMinutes <= 0) return null;
        const next = lane.intervals[i + 1]!;
        const left = pct(iv.to);
        const width = Math.max(0, pct(next.from) - left);
        if (width <= 0) return null;
        return (
          <div
            key={`pause-${i}`}
            data-testid={`lane-pause-${lane.key}-${i}`}
            style={{ position: 'absolute', left: `${left}%`, width: `${width}%`, top: 7, height: 0, borderTop: `1px dashed ${C.textMuted}` }}
          />
        );
      })}

      {lane.returns.map((r, i) => (
        // «↩» возврата над дорожкой в момент вердикта (возвраты в модели — только у prod).
        // aria-hidden: глиф — повтор, сами возвраты и их причины — в деталях шага.
        <span
          key={`ret-${i}`}
          data-testid={`lane-return-${lane.key}-${i}`}
          aria-hidden="true"
          style={{
            position: 'absolute', left: `${pct(r.at)}%`, top: -12, transform: 'translateX(-50%)',
            fontFamily: FONT.mono, fontSize: FS.xxs, color: C.dangerText,
          }}
        >
          ↩
        </span>
      ))}

      {(showMore || showAhead) && (
        <span
          data-testid={`lane-ahead-${lane.key}`}
          style={{
            position: 'absolute', left: `${axis.timeFrac * 100}%`, right: 0, top: 0, bottom: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'flex-end', paddingRight: SP.sm,
            border: `1px dashed ${C.dashed}`, borderRadius: R.sm,
            fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted,
          }}
        >
          {showMore ? `ещё ${lane.plan?.ahead ?? 0}` : 'впереди'}
        </span>
      )}
    </div>
  );
}
